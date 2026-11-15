using System;
using System.Collections.Generic;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// A small correction of one renderer of the craft on top of the single alignment of the whole craft.
    /// One rigid alignment cannot fit a craft whose proportions differ a little from the model's (a shorter fuselage, a
    /// wider wing): the error is spread over everything and some parts end up off the model's surface. Here every renderer
    /// is moved a little (a shift and a stretch along the craft's three axes, both limited) so that its vertices lie
    /// closer to the model's surface, and its texture coordinates are looked up from there.
    /// The cost is the trimmed mean distance from the renderer's vertices to the model, plus a small penalty for moving, so a
    /// renderer that is already on the model (or whose position the surface does not determine, such as a flat panel that
    /// could slide along itself) stays where it is.
    /// </summary>
    internal static class PartRefinement
    {
        private const int MaxPoints = 200;
        private const int MinPoints = 12;
        private const float MaxLogScale = 0.15f;   // the stretch stays within about 0.86 .. 1.16
        private const float MinStretchExtent = 0.3f; // a renderer thinner than this (meters) along an axis is not stretched along it
        private const float KeepFraction = 0.9f;

        /// <summary>The correction of a renderer: p' = center + scale * (p - center) + shift, in craft space.</summary>
        internal struct Correction
        {
            public Vector3 Center;
            public Vector3 Scale;
            public Vector3 Shift;
            public float Before;   // mean distance to the model before (meters)
            public float After;    // and after

            public static Correction None
            {
                get
                {
                    Correction c = new Correction();
                    c.Scale = Vector3.one;
                    return c;
                }
            }

            public Vector3 Apply(Vector3 p)
            {
                return new Vector3(
                    Center.x + Scale.x * (p.x - Center.x) + Shift.x,
                    Center.y + Scale.y * (p.y - Center.y) + Shift.y,
                    Center.z + Scale.z * (p.z - Center.z) + Shift.z);
            }

            /// <summary>The direction a normal gets (inverse of the stretch, as for any normal).</summary>
            public Vector3 ApplyNormal(Vector3 n)
            {
                Vector3 r = new Vector3(n.x / Scale.x, n.y / Scale.y, n.z / Scale.z);
                float m = r.magnitude;
                return m > 1e-9f ? r / m : n;
            }

            public bool Moved
            {
                get { return Shift.sqrMagnitude > 1e-8f || Math.Abs(Scale.x - 1f) > 1e-4f || Math.Abs(Scale.y - 1f) > 1e-4f || Math.Abs(Scale.z - 1f) > 1e-4f; }
            }
        }

        /// <summary>Finds the correction for the vertices of a renderer (craft space). None when there is too little to go by.</summary>
        public static Correction Fit(List<Vector3> craftVertices, TriangleBvh bvh, ModelAlignment alignment, float maxShift)
        {
            Correction none = Correction.None;
            int total = craftVertices.Count;
            if (maxShift <= 0f || total < MinPoints)
            {
                return none;
            }

            int stride = Math.Max(1, total / MaxPoints);
            List<Vector3> points = new List<Vector3>(total / stride + 1);
            Vector3 min = craftVertices[0], max = craftVertices[0], sum = Vector3.zero;
            for (int i = 0; i < total; i += stride)
            {
                Vector3 p = craftVertices[i];
                points.Add(p);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                sum += p;
            }

            if (points.Count < MinPoints)
            {
                return none;
            }

            Vector3 center = sum / points.Count;
            Vector3 extent = max - min;
            Matrix4x4 craftToObj = alignment.CraftToObj;
            float objToMeters = Math.Max(alignment.Scale, 1e-6f);

            float[] limit = { maxShift, maxShift, maxShift, MaxLogScale, MaxLogScale, MaxLogScale };
            if (extent.x < MinStretchExtent) limit[3] = 0f;
            if (extent.y < MinStretchExtent) limit[4] = 0f;
            if (extent.z < MinStretchExtent) limit[5] = 0f;

            float[] x = new float[6];
            float[] scratch = new float[points.Count];
            float before = Cost(points, center, x, craftToObj, objToMeters, bvh, scratch, maxShift, limit, false);
            float cur = before;
            float[] step = { 0.3f * maxShift, 0.3f * maxShift, 0.3f * maxShift, 0.05f, 0.05f, 0.05f };
            for (int round = 0; round < 7; round++)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (limit[i] <= 0f)
                    {
                        continue;
                    }

                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        float old = x[i];
                        float value = old + dir * step[i];
                        if (value > limit[i]) value = limit[i];
                        if (value < -limit[i]) value = -limit[i];
                        if (value == old)
                        {
                            continue;
                        }

                        x[i] = value;
                        float c = Cost(points, center, x, craftToObj, objToMeters, bvh, scratch, maxShift, limit, true);
                        if (c < cur - 1e-5f)
                        {
                            cur = c;
                            break;
                        }

                        x[i] = old;
                    }
                }

                for (int i = 0; i < 6; i++)
                {
                    step[i] *= 0.6f;
                }
            }

            Correction result = new Correction();
            result.Center = center;
            result.Shift = new Vector3(x[0], x[1], x[2]);
            result.Scale = new Vector3(Mathf.Exp(x[3]), Mathf.Exp(x[4]), Mathf.Exp(x[5]));
            result.Before = before;
            result.After = Cost(points, center, x, craftToObj, objToMeters, bvh, scratch, maxShift, limit, false);
            return result.Moved ? result : none;
        }

        // Trimmed mean distance (meters) of the corrected points to the model; with the penalty for moving when asked.
        private static float Cost(List<Vector3> points, Vector3 center, float[] x, Matrix4x4 craftToObj, float objToMeters,
            TriangleBvh bvh, float[] scratch, float maxShift, float[] limit, bool penalty)
        {
            float sx = Mathf.Exp(x[3]), sy = Mathf.Exp(x[4]), sz = Mathf.Exp(x[5]);
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = points[i];
                Vector3 q = new Vector3(
                    center.x + sx * (p.x - center.x) + x[0],
                    center.y + sy * (p.y - center.y) + x[1],
                    center.z + sz * (p.z - center.z) + x[2]);
                scratch[i] = Mathf.Sqrt(bvh.NearestDistanceSquared(craftToObj.MultiplyPoint3x4(q))) * objToMeters;
            }

            Array.Sort(scratch, 0, n);
            int keep = Math.Max(1, (int)(n * KeepFraction));
            float total = 0f;
            for (int i = 0; i < keep; i++)
            {
                total += scratch[i];
            }

            float mean = total / keep;
            if (!penalty)
            {
                return mean;
            }

            float shiftPart = new Vector3(x[0], x[1], x[2]).magnitude / maxShift;
            float stretchPart = (Math.Abs(x[3]) + Math.Abs(x[4]) + Math.Abs(x[5])) / MaxLogScale;
            return mean + 0.02f * maxShift * (shiftPart + stretchPart);
        }
    }
}
