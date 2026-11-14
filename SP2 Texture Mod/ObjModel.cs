using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// A triangle soup read from a Wavefront OBJ file: three corners per triangle and one UV per corner.
    /// Polygons are split into triangles. Optionally mirrored on X to follow the usual convention of
    /// importers into Unity (OBJ is right-handed, Unity is left-handed); the winding is kept consistent.
    /// </summary>
    internal sealed class ObjModel
    {
        public string Name;
        public Vector3[] A;
        public Vector3[] B;
        public Vector3[] C;
        public Vector2[] UvA;
        public Vector2[] UvB;
        public Vector2[] UvC;
        public Bounds Bounds;

        /// <summary>The range of the texture coordinates after they were brought into place, for the log.</summary>
        public string UvRange = "";

        public int TriangleCount
        {
            get { return A.Length; }
        }

        private static readonly char[] Separators = { ' ', '\t' };

        public static ObjModel Load(string path, bool flipX, out string error)
        {
            error = null;

            List<Vector3> positions = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector3> ta = new List<Vector3>();
            List<Vector3> tb = new List<Vector3>();
            List<Vector3> tc = new List<Vector3>();
            List<Vector2> ua = new List<Vector2>();
            List<Vector2> ub = new List<Vector2>();
            List<Vector2> uc = new List<Vector2>();
            int badLines = 0;
            int cornersWithoutUv = 0;

            foreach (string line in File.ReadLines(path))
            {
                if (line.Length < 3)
                {
                    continue;
                }

                char c0 = line[0];
                char c1 = line[1];

                if (c0 == 'v' && c1 == ' ')
                {
                    string[] p = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                    float x, y, z;
                    if (p.Length >= 4 && TryFloat(p[1], out x) && TryFloat(p[2], out y) && TryFloat(p[3], out z))
                    {
                        positions.Add(new Vector3(x, y, z));
                    }
                    else
                    {
                        badLines++;
                    }
                }
                else if (c0 == 'v' && c1 == 't' && line[2] == ' ')
                {
                    string[] p = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                    float u, v;
                    if (p.Length >= 3 && TryFloat(p[1], out u) && TryFloat(p[2], out v))
                    {
                        uvs.Add(new Vector2(u, v));
                    }
                    else
                    {
                        badLines++;
                    }
                }
                else if (c0 == 'f' && c1 == ' ')
                {
                    string[] p = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                    int count = p.Length - 1;
                    if (count < 3)
                    {
                        badLines++;
                        continue;
                    }

                    int[] vi = new int[count];
                    int[] ti = new int[count];
                    bool ok = true;
                    for (int i = 0; i < count; i++)
                    {
                        if (!ParseCorner(p[i + 1], positions.Count, uvs.Count, out vi[i], out ti[i]))
                        {
                            ok = false;
                            break;
                        }
                    }

                    if (!ok)
                    {
                        badLines++;
                        continue;
                    }

                    // Fan triangulation: (0, i, i + 1).
                    for (int i = 1; i < count - 1; i++)
                    {
                        Vector2 uva = ti[0] >= 0 ? uvs[ti[0]] : Vector2.zero;
                        Vector2 uvb = ti[i] >= 0 ? uvs[ti[i]] : Vector2.zero;
                        Vector2 uvc = ti[i + 1] >= 0 ? uvs[ti[i + 1]] : Vector2.zero;
                        if (ti[0] < 0) cornersWithoutUv++;
                        if (ti[i] < 0) cornersWithoutUv++;
                        if (ti[i + 1] < 0) cornersWithoutUv++;

                        AddTriangle(ta, tb, tc, ua, ub, uc,
                            positions[vi[0]], positions[vi[i]], positions[vi[i + 1]], uva, uvb, uvc, flipX);
                    }
                }
            }

            if (ta.Count == 0)
            {
                error = "the file has no faces (f lines)";
                return null;
            }

            if (uvs.Count == 0)
            {
                error = "the file has no texture coordinates (vt lines); export the OBJ with UVs";
                return null;
            }

            if (cornersWithoutUv > ta.Count)
            {
                error = "most faces have no texture coordinates; export the OBJ with UVs";
                return null;
            }

            ObjModel model = new ObjModel();
            model.Name = Path.GetFileName(path);
            model.A = ta.ToArray();
            model.B = tb.ToArray();
            model.C = tc.ToArray();
            model.UvA = ua.ToArray();
            model.UvB = ub.ToArray();
            model.UvC = uc.ToArray();

            Vector3 min = model.A[0];
            Vector3 max = model.A[0];
            for (int i = 0; i < model.A.Length; i++)
            {
                Grow(ref min, ref max, model.A[i]);
                Grow(ref min, ref max, model.B[i]);
                Grow(ref min, ref max, model.C[i]);
            }

            model.Bounds = new Bounds();
            model.Bounds.SetMinMax(min, max);

            // Many exports write v in [-1, 0] (that is v - 1) and some islands sit a little outside [0, 1].
            // A texture that repeats shows the same picture for v and v + 1, so the numbers are moved by a whole
            // amount (the one that puts the middle of the texture coordinates inside [0, 1]); the texture itself
            // must then repeat too, because some coordinates stay a little outside.
            float shiftU;
            float shiftV;
            WholeShift(model, out shiftU, out shiftV);
            if (shiftU != 0f || shiftV != 0f)
            {
                Vector2 shift = new Vector2(shiftU, shiftV);
                for (int i = 0; i < model.UvA.Length; i++)
                {
                    model.UvA[i] += shift;
                    model.UvB[i] += shift;
                    model.UvC[i] += shift;
                }
            }

            Vector2 uvMin = model.UvA[0];
            Vector2 uvMax = model.UvA[0];
            for (int i = 0; i < model.UvA.Length; i++)
            {
                uvMin = Vector2.Min(uvMin, Vector2.Min(model.UvA[i], Vector2.Min(model.UvB[i], model.UvC[i])));
                uvMax = Vector2.Max(uvMax, Vector2.Max(model.UvA[i], Vector2.Max(model.UvB[i], model.UvC[i])));
            }

            model.UvRange = "u " + uvMin.x.ToString("0.###", CultureInfo.InvariantCulture) + ".." + uvMax.x.ToString("0.###", CultureInfo.InvariantCulture)
                + ", v " + uvMin.y.ToString("0.###", CultureInfo.InvariantCulture) + ".." + uvMax.y.ToString("0.###", CultureInfo.InvariantCulture);
            Plugin.Log.LogInfo("[OBJ] " + model.Name + ": texture coordinates " + model.UvRange
                + " after shifting them by (" + shiftU.ToString("0", CultureInfo.InvariantCulture) + ", " + shiftV.ToString("0", CultureInfo.InvariantCulture) + ")");

            if (badLines > 0)
            {
                Plugin.Log.LogWarning("[OBJ] " + model.Name + ": " + badLines + " line(s) could not be read and were skipped");
            }

            return model;
        }

        // The whole-number shift of u and v that puts the median of each inside [0, 1).
        private static void WholeShift(ObjModel model, out float shiftU, out float shiftV)
        {
            int step = Math.Max(1, model.UvA.Length / 20000);
            List<float> us = new List<float>();
            List<float> vs = new List<float>();
            for (int i = 0; i < model.UvA.Length; i += step)
            {
                us.Add(model.UvA[i].x);
                vs.Add(model.UvA[i].y);
            }

            us.Sort();
            vs.Sort();
            shiftU = -Mathf.Floor(us[us.Count / 2]);
            shiftV = -Mathf.Floor(vs[vs.Count / 2]);
        }

        private static void AddTriangle(List<Vector3> ta, List<Vector3> tb, List<Vector3> tc,
            List<Vector2> ua, List<Vector2> ub, List<Vector2> uc,
            Vector3 pa, Vector3 pb, Vector3 pc, Vector2 uva, Vector2 uvb, Vector2 uvc, bool flipX)
        {
            if (flipX)
            {
                pa.x = -pa.x;
                pb.x = -pb.x;
                pc.x = -pc.x;

                // Mirroring reverses the winding: swap two corners to keep the normal pointing outwards.
                Vector3 tp = pb; pb = pc; pc = tp;
                Vector2 tu = uvb; uvb = uvc; uvc = tu;
            }

            ta.Add(pa);
            tb.Add(pb);
            tc.Add(pc);
            ua.Add(uva);
            ub.Add(uvb);
            uc.Add(uvc);
        }

        private static void Grow(ref Vector3 min, ref Vector3 max, Vector3 p)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // A face corner is "v", "v/vt", "v//vn" or "v/vt/vn"; indexes are 1-based, negative ones count from the end.
        private static bool ParseCorner(string token, int vertexCount, int uvCount, out int vertexIndex, out int uvIndex)
        {
            vertexIndex = -1;
            uvIndex = -1;

            int slash = token.IndexOf('/');
            string vertexText = slash < 0 ? token : token.Substring(0, slash);

            int v;
            if (!int.TryParse(vertexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                return false;
            }

            vertexIndex = v > 0 ? v - 1 : vertexCount + v;
            if (vertexIndex < 0 || vertexIndex >= vertexCount)
            {
                return false;
            }

            if (slash >= 0)
            {
                int slash2 = token.IndexOf('/', slash + 1);
                string uvText = slash2 < 0 ? token.Substring(slash + 1) : token.Substring(slash + 1, slash2 - slash - 1);
                int t;
                if (uvText.Length > 0 && int.TryParse(uvText, NumberStyles.Integer, CultureInfo.InvariantCulture, out t))
                {
                    int index = t > 0 ? t - 1 : uvCount + t;
                    if (index >= 0 && index < uvCount)
                    {
                        uvIndex = index;
                    }
                }
            }

            return true;
        }
    }
}
