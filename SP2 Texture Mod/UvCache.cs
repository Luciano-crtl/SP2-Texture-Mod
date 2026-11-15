using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// The texture coordinates written when a texture was applied to a craft, kept per craft next to its saved
    /// texture (Crafts/&lt;name&gt;.uv). Each entry is one renderer of one part: the part's id, the renderer's place in the
    /// part, and a fingerprint of the mesh's own vertices (not of where the part sits), so the coordinates stay valid
    /// when the craft is posed differently afterwards (a swing wing swept back, landing gear folded).
    /// With it, flying the craft or opening it in the designer needs neither the OBJ model nor a new alignment: the
    /// model is only read for a renderer that has no entry (a part added since, or a mesh that was rebuilt).
    /// The file is only valid for the alignment it was made with: it carries a signature of the model file and of
    /// every setting the coordinates depend on, and is ignored when that no longer matches.
    /// </summary>
    internal sealed class UvCache
    {
        // Change it when the way the coordinates are worked out changes (old files are then ignored).
        private const int AlgorithmVersion = 1;
        private const string Magic = "SP2UVCACHE";

        private sealed class Entry
        {
            public int VertexCount;
            public ulong Hash;
            public Vector2[] Uvs;
        }

        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private readonly List<Vector3> _scratch = new List<Vector3>();
        private readonly List<Vector2> _uvScratch = new List<Vector2>();

        public readonly string Signature;

        public UvCache(string signature)
        {
            Signature = signature;
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        /// <summary>Everything the coordinates depend on, as one short text.</summary>
        public static string MakeSignature(string modelPath, bool flipX, Matrix4x4 objToCraft, bool normalsInverted,
            bool frontFaces, bool project, float rayStart, float facingWeight, float maxDistance,
            float neutralBeyond = 0f, float neutralU = 0f, float neutralV = 0f, float partRefine = 0f)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("v").Append(AlgorithmVersion).Append('|');
            try
            {
                FileInfo info = new FileInfo(modelPath);
                sb.Append(info.Name).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                sb.Append(modelPath);
            }

            sb.Append('|').Append(flipX ? 1 : 0).Append(normalsInverted ? 1 : 0).Append(frontFaces ? 1 : 0).Append(project ? 1 : 0);
            sb.Append('|').Append(F(rayStart)).Append(',').Append(F(facingWeight)).Append(',').Append(F(maxDistance)).Append('|');
            if (neutralBeyond > 0f)
            {
                sb.Append("n").Append(F(neutralBeyond)).Append(',').Append(F(neutralU)).Append(',').Append(F(neutralV)).Append('|');
            }

            if (partRefine > 0f)
            {
                sb.Append("r").Append(F(partRefine)).Append('|');
            }

            for (int i = 0; i < 16; i++)
            {
                sb.Append(F(objToCraft[i])).Append(',');
            }

            return Fnv(sb.ToString()).ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>The signature of what an assignment was saved with.</summary>
        public static string MakeSignature(string modelPath, CraftAssignment a)
        {
            return MakeSignature(modelPath, a.FlipModelX, a.ObjToCraft, a.NormalsInverted, a.FrontFaces, a.Project,
                a.RayStart, a.FacingWeight, a.MaxDistance, a.NeutralBeyond, a.NeutralU, a.NeutralV, a.PartRefine);
        }

        /// <summary>Writes the stored coordinates of this renderer into the mesh. False if there are none for this exact mesh.</summary>
        public bool Apply(int partId, int ordinal, Mesh mesh)
        {
            Entry entry;
            if (!_entries.TryGetValue(KeyOf(partId, ordinal), out entry) || entry.VertexCount != mesh.vertexCount)
            {
                return false;
            }

            if (HashVertices(mesh, _scratch) != entry.Hash)
            {
                return false;
            }

            mesh.SetUVs(0, entry.Uvs);
            return true;
        }

        /// <summary>Keeps the coordinates just written into the mesh (replacing what was stored for this renderer).</summary>
        public void Remember(int partId, int ordinal, Mesh mesh)
        {
            Entry entry = new Entry();
            entry.VertexCount = mesh.vertexCount;
            entry.Hash = HashVertices(mesh, _scratch);
            mesh.GetUVs(0, _uvScratch);
            entry.Uvs = _uvScratch.ToArray();
            _entries[KeyOf(partId, ordinal)] = entry;
        }

        /// <summary>The cache of a craft, or null if there is none, it is damaged, or it was made for another alignment.</summary>
        public static UvCache Load(string craftName, string signature)
        {
            string path = CraftAssignment.UvFilePathFor(craftName);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (BinaryReader r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)), Encoding.UTF8))
                {
                    if (r.ReadString() != Magic || r.ReadInt32() != AlgorithmVersion)
                    {
                        return null;
                    }

                    string stored = r.ReadString();
                    if (stored != signature)
                    {
                        Plugin.Log.LogInfo("[UVCACHE] the saved coordinates of '" + craftName + "' were made with another alignment or model: not used");
                        return null;
                    }

                    UvCache cache = new UvCache(signature);
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int partId = r.ReadInt32();
                        int ordinal = r.ReadInt32();
                        Entry entry = new Entry();
                        entry.VertexCount = r.ReadInt32();
                        entry.Hash = r.ReadUInt64();
                        if (entry.VertexCount < 0 || entry.VertexCount > 10000000)
                        {
                            return null;
                        }

                        entry.Uvs = new Vector2[entry.VertexCount];
                        for (int v = 0; v < entry.VertexCount; v++)
                        {
                            float u = r.ReadSingle();
                            float w = r.ReadSingle();
                            entry.Uvs[v] = new Vector2(u, w);
                        }

                        cache._entries[KeyOf(partId, ordinal)] = entry;
                    }

                    Plugin.Log.LogInfo("[UVCACHE] " + cache.Count + " renderer(s) of '" + craftName + "' have saved coordinates");
                    return cache;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UVCACHE] could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        public bool Save(string craftName)
        {
            string path = CraftAssignment.UvFilePathFor(craftName);
            try
            {
                Directory.CreateDirectory(CraftAssignment.FolderPath);
                MemoryStream stream = new MemoryStream();
                using (BinaryWriter w = new BinaryWriter(stream, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(AlgorithmVersion);
                    w.Write(Signature);
                    w.Write(_entries.Count);
                    foreach (KeyValuePair<long, Entry> pair in _entries)
                    {
                        w.Write((int)(pair.Key / 4096L));
                        w.Write((int)(pair.Key % 4096L));
                        w.Write(pair.Value.VertexCount);
                        w.Write(pair.Value.Hash);
                        for (int v = 0; v < pair.Value.Uvs.Length; v++)
                        {
                            w.Write(pair.Value.Uvs[v].x);
                            w.Write(pair.Value.Uvs[v].y);
                        }
                    }

                    w.Flush();
                    File.WriteAllBytes(path, stream.ToArray());
                }

                Plugin.Log.LogInfo("[UVCACHE] " + _entries.Count + " renderer(s) saved for '" + craftName + "'");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UVCACHE] could not write " + path + ": " + ex.Message);
                return false;
            }
        }

        // The part id and the renderer's place in the part (a part has a handful of renderers).
        private static long KeyOf(int partId, int ordinal)
        {
            return (long)partId * 4096L + Math.Min(Math.Max(ordinal, 0), 4095);
        }

        // FNV-1a over the vertices rounded to a millimeter: the same mesh built again gives the same number,
        // a changed one (another size or shape of the part) a different one.
        private static ulong HashVertices(Mesh mesh, List<Vector3> scratch)
        {
            scratch.Clear();
            mesh.GetVertices(scratch);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < scratch.Count; i++)
            {
                hash = Mix(hash, Quantize(scratch[i].x));
                hash = Mix(hash, Quantize(scratch[i].y));
                hash = Mix(hash, Quantize(scratch[i].z));
            }

            return Mix(hash, (uint)scratch.Count);
        }

        private static uint Quantize(float value)
        {
            return unchecked((uint)(int)Math.Round(value * 1000.0));
        }

        private static ulong Mix(ulong hash, uint value)
        {
            unchecked
            {
                hash ^= value;
                return hash * 1099511628211UL;
            }
        }

        private static ulong Fnv(string text)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++)
            {
                hash = Mix(hash, text[i]);
            }

            return hash;
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
