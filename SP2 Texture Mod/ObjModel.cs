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

            // Read as bytes and parsed by hand: splitting every line into strings took 23 s with a model of 380 thousand
            // triangles in the game's Mono.
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                error = "the file could not be read: " + ex.Message;
                return null;
            }

            List<bool> complete = new List<bool>();
            Parse(data, positions, uvs, ta, tb, tc, ua, ub, uc, complete, flipX, ref badLines, ref cornersWithoutUv);
            data = null;

            // Many faces without texture coordinates: read the file again with the first (slow) reader, to tell a file that
            // really lacks them from a mistake of the fast reader. If they disagree the slow one is used.
            if (ta.Count > 0 && cornersWithoutUv > ta.Count)
            {
                List<Vector3> sPositions = new List<Vector3>();
                List<Vector2> sUvs = new List<Vector2>();
                List<Vector3> sa = new List<Vector3>();
                List<Vector3> sb = new List<Vector3>();
                List<Vector3> sc = new List<Vector3>();
                List<Vector2> sua = new List<Vector2>();
                List<Vector2> sub = new List<Vector2>();
                List<Vector2> suc = new List<Vector2>();
                List<bool> sComplete = new List<bool>();
                int sBad = 0;
                int sWithout = 0;
                ParseSlow(path, sPositions, sUvs, sa, sb, sc, sua, sub, suc, sComplete, flipX, ref sBad, ref sWithout);
                Plugin.Log.LogInfo("[OBJ] " + Path.GetFileName(path) + ": fast reader " + positions.Count + " vertices, " + uvs.Count + " vt, "
                    + ta.Count + " triangles, " + cornersWithoutUv + " corners without a vt; slow reader " + sPositions.Count + " vertices, "
                    + sUvs.Count + " vt, " + sa.Count + " triangles, " + sWithout + " corners without a vt");
                if (sWithout < cornersWithoutUv || sUvs.Count != uvs.Count)
                {
                    Plugin.Log.LogWarning("[OBJ] the two readers disagree: using the slow one");
                    positions = sPositions; uvs = sUvs; ta = sa; tb = sb; tc = sc; ua = sua; ub = sub; uc = suc;
                    complete = sComplete; badLines = sBad; cornersWithoutUv = sWithout;
                }
            }

            return Finish(Path.GetFileName(path), "OBJ", ta, tb, tc, ua, ub, uc, complete, uvs.Count, badLines, out error);
        }

        /// <summary>
        /// Turns a triangle soup (one UV per corner, a flag per triangle telling whether all three corners had one) into a
        /// model: refuses a soup with almost no UVs, leaves out the triangles without, takes the bounds and brings the
        /// texture coordinates into place. Shared by the OBJ and the glTF readers.
        /// </summary>
        internal static ObjModel Finish(string name, string tag, List<Vector3> ta, List<Vector3> tb, List<Vector3> tc,
            List<Vector2> ua, List<Vector2> ub, List<Vector2> uc, List<bool> complete, int uvCount, int badLines, out string error)
        {
            error = null;
            if (ta.Count == 0)
            {
                error = "the file has no faces (f lines)";
                return null;
            }

            if (uvCount == 0)
            {
                error = "the file has no texture coordinates (vt lines); export the model with UVs";
                return null;
            }

            // Triangles without texture coordinates (other objects of the file: glass, wheels, a cockpit...) are left out;
            // the model is useless only when almost none has them.
            int withUv = 0;
            for (int i = 0; i < complete.Count; i++)
            {
                if (complete[i]) withUv++;
            }

            if (withUv == 0 || withUv < 0.2 * ta.Count)
            {
                error = "only " + withUv + " of " + ta.Count + " triangles have texture coordinates (the file has " + uvCount
                    + " vt lines); export the model with UVs";
                return null;
            }

            if (withUv < ta.Count)
            {
                Plugin.Log.LogWarning("[" + tag + "] " + name + ": " + (ta.Count - withUv) + " of " + ta.Count
                    + " triangles have no texture coordinates and were left out (probably other objects of the file)");
                int kept = 0;
                for (int i = 0; i < ta.Count; i++)
                {
                    if (!complete[i]) continue;
                    ta[kept] = ta[i]; tb[kept] = tb[i]; tc[kept] = tc[i];
                    ua[kept] = ua[i]; ub[kept] = ub[i]; uc[kept] = uc[i];
                    kept++;
                }

                ta.RemoveRange(kept, ta.Count - kept); tb.RemoveRange(kept, tb.Count - kept); tc.RemoveRange(kept, tc.Count - kept);
                ua.RemoveRange(kept, ua.Count - kept); ub.RemoveRange(kept, ub.Count - kept); uc.RemoveRange(kept, uc.Count - kept);
            }

            ObjModel model = new ObjModel();
            model.Name = name;
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
            Plugin.Log.LogInfo("[" + tag + "] " + model.Name + ": texture coordinates " + model.UvRange
                + " after shifting them by (" + shiftU.ToString("0", CultureInfo.InvariantCulture) + ", " + shiftV.ToString("0", CultureInfo.InvariantCulture) + ")");

            if (badLines > 0)
            {
                Plugin.Log.LogWarning("[" + tag + "] " + model.Name + ": " + badLines + " line(s) could not be read and were skipped");
            }

            return model;
        }

        // Lines "v x y z", "vt u v" and "f a b c ..." (corners "v", "v/vt", "v//vn" or "v/vt/vn", 1-based or negative);
        // everything else (comments, vn, o, g, usemtl...) is skipped. Polygons are fanned into triangles.
        private static void Parse(byte[] d, List<Vector3> positions, List<Vector2> uvs,
            List<Vector3> ta, List<Vector3> tb, List<Vector3> tc, List<Vector2> ua, List<Vector2> ub, List<Vector2> uc,
            List<bool> complete, bool flipX, ref int badLines, ref int cornersWithoutUv)
        {
            int n = d.Length;
            int pos = 0;
            int[] vi = new int[32];
            int[] ti = new int[32];
            while (pos < n)
            {
                int end = pos;
                while (end < n && d[end] != (byte)'\n' && d[end] != (byte)'\r')
                {
                    end++;
                }

                int p = pos;
                pos = end + 1;
                while (p < end && (d[p] == (byte)' ' || d[p] == (byte)'\t'))
                {
                    p++;
                }

                if (end - p < 3)
                {
                    continue;
                }

                byte c0 = d[p];
                byte c1 = d[p + 1];
                if (c0 == (byte)'v' && c1 == (byte)' ')
                {
                    p += 2;
                    float x, y, z;
                    if (ReadFloat(d, ref p, end, out x) && ReadFloat(d, ref p, end, out y) && ReadFloat(d, ref p, end, out z))
                    {
                        positions.Add(new Vector3(x, y, z));
                    }
                    else
                    {
                        badLines++;
                    }
                }
                else if (c0 == (byte)'v' && c1 == (byte)'t' && d[p + 2] == (byte)' ')
                {
                    p += 3;
                    float u, v;
                    if (ReadFloat(d, ref p, end, out u) && ReadFloat(d, ref p, end, out v))
                    {
                        uvs.Add(new Vector2(u, v));
                    }
                    else
                    {
                        badLines++;
                    }
                }
                else if (c0 == (byte)'f' && c1 == (byte)' ')
                {
                    p += 2;
                    int count = 0;
                    bool ok = true;
                    while (true)
                    {
                        while (p < end && (d[p] == (byte)' ' || d[p] == (byte)'\t'))
                        {
                            p++;
                        }

                        if (p >= end)
                        {
                            break;
                        }

                        if (count == vi.Length)
                        {
                            Array.Resize(ref vi, count * 2);
                            Array.Resize(ref ti, count * 2);
                        }

                        if (!ReadCorner(d, ref p, end, positions.Count, uvs.Count, out vi[count], out ti[count]))
                        {
                            ok = false;
                            break;
                        }

                        count++;
                    }

                    if (!ok || count < 3)
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
                        complete.Add(ti[0] >= 0 && ti[i] >= 0 && ti[i + 1] >= 0);
                    }
                }
            }
        }

        // The first reader (a string per line, float.TryParse), kept as a cross-check: it is used when the fast one finds
        // that many faces have no texture coordinates, to tell a file without them from a mistake of the fast reader.
        private static void ParseSlow(string path, List<Vector3> positions, List<Vector2> uvs,
            List<Vector3> ta, List<Vector3> tb, List<Vector3> tc, List<Vector2> ua, List<Vector2> ub, List<Vector2> uc,
            List<bool> complete, bool flipX, ref int badLines, ref int cornersWithoutUv)
        {
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
                        complete.Add(ti[0] >= 0 && ti[i] >= 0 && ti[i + 1] >= 0);
                    }
                }
            }
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

        private static readonly double[] Powers = BuildPowers();

        private static double[] BuildPowers()
        {
            double[] table = new double[64];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = Math.Pow(10.0, i);
            }

            return table;
        }

        // A decimal number ("1", "-0.25", ".5", "5.", "1e-3") followed by a space, a tab or the end of the line.
        private static bool ReadFloat(byte[] d, ref int p, int end, out float value)
        {
            value = 0f;
            while (p < end && (d[p] == (byte)' ' || d[p] == (byte)'\t'))
            {
                p++;
            }

            if (p >= end)
            {
                return false;
            }

            bool negative = false;
            if (d[p] == (byte)'-')
            {
                negative = true;
                p++;
            }
            else if (d[p] == (byte)'+')
            {
                p++;
            }

            const long Limit = 100000000000000000L; // more digits than a float holds are dropped
            long mantissa = 0;
            int exponent = 0;
            bool any = false;
            while (p < end && d[p] >= (byte)'0' && d[p] <= (byte)'9')
            {
                any = true;
                if (mantissa < Limit)
                {
                    mantissa = mantissa * 10 + (d[p] - (byte)'0');
                }
                else
                {
                    exponent++;
                }

                p++;
            }

            if (p < end && d[p] == (byte)'.')
            {
                p++;
                while (p < end && d[p] >= (byte)'0' && d[p] <= (byte)'9')
                {
                    any = true;
                    if (mantissa < Limit)
                    {
                        mantissa = mantissa * 10 + (d[p] - (byte)'0');
                        exponent--;
                    }

                    p++;
                }
            }

            if (!any)
            {
                return false;
            }

            if (p < end && (d[p] == (byte)'e' || d[p] == (byte)'E'))
            {
                p++;
                bool negativeExponent = false;
                if (p < end && (d[p] == (byte)'-' || d[p] == (byte)'+'))
                {
                    negativeExponent = d[p] == (byte)'-';
                    p++;
                }

                int e = 0;
                bool digits = false;
                while (p < end && d[p] >= (byte)'0' && d[p] <= (byte)'9')
                {
                    digits = true;
                    if (e < 1000)
                    {
                        e = e * 10 + (d[p] - (byte)'0');
                    }

                    p++;
                }

                if (!digits)
                {
                    return false;
                }

                exponent += negativeExponent ? -e : e;
            }

            if (p < end && d[p] != (byte)' ' && d[p] != (byte)'\t')
            {
                return false;
            }

            double result = mantissa;
            if (exponent > 0)
            {
                result *= exponent < Powers.Length ? Powers[exponent] : Math.Pow(10.0, exponent);
            }
            else if (exponent < 0)
            {
                result /= -exponent < Powers.Length ? Powers[-exponent] : Math.Pow(10.0, -exponent);
            }

            value = (float)(negative ? -result : result);
            return true;
        }

        // A face corner: "v", "v/vt", "v//vn" or "v/vt/vn". Indexes are 1-based, negative ones count from the end.
        private static bool ReadCorner(byte[] d, ref int p, int end, int vertexCount, int uvCount, out int vertexIndex, out int uvIndex)
        {
            vertexIndex = -1;
            uvIndex = -1;

            int v;
            if (!ReadInt(d, ref p, end, out v))
            {
                return false;
            }

            vertexIndex = v > 0 ? v - 1 : vertexCount + v;
            if (vertexIndex < 0 || vertexIndex >= vertexCount)
            {
                return false;
            }

            if (p < end && d[p] == (byte)'/')
            {
                p++;
                int t;
                if (p < end && d[p] != (byte)'/' && d[p] != (byte)' ' && d[p] != (byte)'\t' && ReadInt(d, ref p, end, out t))
                {
                    int index = t > 0 ? t - 1 : uvCount + t;
                    if (index >= 0 && index < uvCount)
                    {
                        uvIndex = index;
                    }
                }
            }

            // The rest of the token (the normal index) is of no use here.
            while (p < end && d[p] != (byte)' ' && d[p] != (byte)'\t')
            {
                p++;
            }

            return true;
        }

        private static bool ReadInt(byte[] d, ref int p, int end, out int value)
        {
            value = 0;
            bool negative = false;
            if (p < end && d[p] == (byte)'-')
            {
                negative = true;
                p++;
            }
            else if (p < end && d[p] == (byte)'+')
            {
                p++;
            }

            bool any = false;
            long number = 0;
            while (p < end && d[p] >= (byte)'0' && d[p] <= (byte)'9')
            {
                any = true;
                if (number < 1000000000L)
                {
                    number = number * 10 + (d[p] - (byte)'0');
                }

                p++;
            }

            if (!any)
            {
                return false;
            }

            value = (int)(negative ? -number : number);
            return true;
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

        internal static void AddTriangle(List<Vector3> ta, List<Vector3> tb, List<Vector3> tc,
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
    }
}
