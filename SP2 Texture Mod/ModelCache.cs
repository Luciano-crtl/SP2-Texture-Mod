using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// The parsed OBJ and its index (BVH), kept between uses: reading a large model takes a moment, and the
    /// designer, the automatic re-apply and flight all need the same one. Only the last model is kept; it is
    /// read again when the file, its modification time or the mirror setting changes.
    /// </summary>
    internal static class ModelCache
    {
        private static string _path;
        private static DateTime _stamp;
        private static bool _flip;
        private static ObjModel _model;
        private static TriangleBvh _bvh;

        /// <summary>Path of the model in the cache (null if none).</summary>
        public static string LoadedPath
        {
            get { return _model != null ? _path : null; }
        }

        /// <summary>Whether the cached model was mirrored on X when it was read.</summary>
        public static bool LoadedFlip
        {
            get { return _flip; }
        }

        public static bool Get(string path, bool flip, out ObjModel model, out TriangleBvh bvh, out string error)
        {
            model = null;
            bvh = null;
            error = null;

            try
            {
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(path);
                if (_model == null || _path != path || _stamp != stamp || _flip != flip)
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    string loadError;
                    ObjModel loaded = IsGltf(path) ? LoadGltf(path, flip, out loadError) : ObjModel.Load(path, flip, out loadError);
                    if (loaded == null)
                    {
                        error = loadError;
                        Plugin.Log.LogWarning("[TEX] model not used: " + loadError);
                        return false;
                    }

                    TriangleBvh index = new TriangleBvh(loaded);
                    _model = loaded;
                    _bvh = index;
                    _path = path;
                    _stamp = stamp;
                    _flip = flip;

                    Bounds b = loaded.Bounds;
                    Plugin.Log.LogInfo("[TEX] model '" + loaded.Name + "': " + loaded.TriangleCount + " triangles, size ("
                        + F(b.size.x) + ", " + F(b.size.y) + ", " + F(b.size.z) + "), mirrored on X=" + flip
                        + ", loaded and indexed in " + watch.ElapsedMilliseconds + " ms");
                }

                model = _model;
                bvh = _bvh;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Plugin.Log.LogWarning("[TEX] model not used: " + ex.Message);
                return false;
            }
        }

        /// <summary>A .glb or .gltf file.</summary>
        public static bool IsGltf(string path)
        {
            return GltfDocument.IsGltf(path);
        }

        private static ObjModel LoadGltf(string path, bool flip, out string error)
        {
            GltfDocument document = GltfDocument.Open(path, out error);
            if (document == null)
            {
                return null;
            }

            GltfDocument.Primary primary = document.FindPrimary(out error);
            if (primary == null)
            {
                return null;
            }

            return document.BuildModel(primary, flip, out error);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
