using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Finds the transform that puts a War Thunder OBJ model on top of the craft the user built, with no
    /// manual work: the best of the 24 axis-aligned orientations (each scaled to the craft's bounds), then
    /// a small coordinate-descent refinement of position, uniform scale and rotation. The quality measure
    /// is the mean distance from sample points of the craft's surface to the model's surface (the worst
    /// 15% are ignored, so parts the model does not have, like pylons or gear, do not spoil the fit).
    /// </summary>
    internal sealed class ModelAlignment
    {
        public Matrix4x4 ObjToCraft;
        public Matrix4x4 CraftToObj;

        /// <summary>Meters of craft per unit of the OBJ.</summary>
        public float Scale;

        /// <summary>Trimmed mean distance in meters after fitting.</summary>
        public float MeanDistance;

        public string Summary;

        private static readonly Matrix4x4[] Orientations = BuildOrientations();

        public static ModelAlignment Fit(ObjModel model, TriangleBvh bvh, List<Vector3> samples, Bounds craftBounds)
        {
            Vector3 objCenter = model.Bounds.center;
            Vector3 objSize = model.Bounds.size;
            Vector3 craftCenter = craftBounds.center;
            Vector3 craftSize = craftBounds.size;
            float craftMax = Math.Max(craftSize.x, Math.Max(craftSize.y, craftSize.z));
            float[] scratch = new float[samples.Count];

            // 1. Best of the 24 orientations, each scaled so the bounding boxes have a similar size.
            float bestCost = float.MaxValue;
            int bestOrientation = 0;
            float[] bestX = null;
            for (int k = 0; k < Orientations.Length; k++)
            {
                Vector3 rotated = Abs(Orientations[k].MultiplyVector(objSize));
                float sx = craftSize.x / Math.Max(rotated.x, 1e-4f);
                float sy = craftSize.y / Math.Max(rotated.y, 1e-4f);
                float sz = craftSize.z / Math.Max(rotated.z, 1e-4f);
                float s0 = Mathf.Pow(Math.Max(sx * sy * sz, 1e-9f), 1f / 3f);

                float[] x = { craftCenter.x, craftCenter.y, craftCenter.z, Mathf.Log(s0), 0f, 0f, 0f };
                float cost = Evaluate(bvh, samples, Build(Orientations[k], objCenter, x), Mathf.Exp(x[3]), scratch);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestOrientation = k;
                    bestX = x;
                }
            }

            float startCost = bestCost;

            // 2. Coordinate descent on position (3), log scale (1) and small rotations (3).
            float[] p = (float[])bestX.Clone();
            float cur = bestCost;
            float[] step = { 0.05f * craftMax, 0.05f * craftMax, 0.05f * craftMax, 0.04f, 3f, 3f, 3f };
            for (int round = 0; round < 10; round++)
            {
                for (int i = 0; i < 7; i++)
                {
                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        float old = p[i];
                        p[i] = old + dir * step[i];
                        float c = Evaluate(bvh, samples, Build(Orientations[bestOrientation], objCenter, p), Mathf.Exp(p[3]), scratch);
                        if (c < cur)
                        {
                            cur = c;
                            break;
                        }

                        p[i] = old;
                    }
                }

                for (int i = 0; i < 7; i++)
                {
                    step[i] *= 0.6f;
                }
            }

            ModelAlignment result = new ModelAlignment();
            result.ObjToCraft = Build(Orientations[bestOrientation], objCenter, p);
            result.CraftToObj = result.ObjToCraft.inverse;
            result.Scale = Mathf.Exp(p[3]);
            result.MeanDistance = cur;
            result.Summary = "orientation #" + bestOrientation + " scale " + F(result.Scale)
                + " rotation(" + F(p[4]) + ", " + F(p[5]) + ", " + F(p[6]) + ") deg offset from craft center("
                + F(p[0] - craftCenter.x) + ", " + F(p[1] - craftCenter.y) + ", " + F(p[2] - craftCenter.z) + ") m"
                + " | mean distance " + F(startCost) + " m at the first guess, " + F(cur) + " m after refining ("
                + F(100f * cur / Math.Max(craftMax, 1e-4f)) + "% of the craft's largest size, " + samples.Count + " samples)";
            return result;
        }

        /// <summary>
        /// Applies the manual correction from the settings on top of the automatic fit: rotation and scale
        /// about the craft's center, then a shift in meters.
        /// </summary>
        public ModelAlignment WithAdjustment(Vector3 craftCenter, Vector3 offset, Vector3 eulerDegrees, float scale)
        {
            Matrix4x4 adjust = Matrix4x4.Translate(craftCenter + offset)
                * Matrix4x4.Rotate(Quaternion.Euler(eulerDegrees))
                * Matrix4x4.Scale(new Vector3(scale, scale, scale))
                * Matrix4x4.Translate(-craftCenter);

            ModelAlignment result = new ModelAlignment();
            result.ObjToCraft = adjust * ObjToCraft;
            result.CraftToObj = result.ObjToCraft.inverse;
            result.Scale = result.ObjToCraft.MultiplyVector(Vector3.right).magnitude;
            result.MeanDistance = MeanDistance;
            result.Summary = Summary;
            return result;
        }

        private static float Evaluate(TriangleBvh bvh, List<Vector3> samples, Matrix4x4 objToCraft, float scale, float[] scratch)
        {
            Matrix4x4 inverse = objToCraft.inverse;
            int n = samples.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 q = inverse.MultiplyPoint3x4(samples[i]);
                scratch[i] = Mathf.Sqrt(bvh.NearestDistanceSquared(q)) * scale;
            }

            Array.Sort(scratch, 0, n);
            int keep = Math.Max(1, (int)(n * 0.85f));
            float sum = 0f;
            for (int i = 0; i < keep; i++)
            {
                sum += scratch[i];
            }

            return sum / keep;
        }

        // craft = translate(t) * smallRotation * orientation * scale(s) * translate(-objCenter)
        private static Matrix4x4 Build(Matrix4x4 orientation, Vector3 objCenter, float[] x)
        {
            Vector3 t = new Vector3(x[0], x[1], x[2]);
            float s = Mathf.Exp(x[3]);
            Quaternion small = Quaternion.Euler(x[4], x[5], x[6]);
            return Matrix4x4.TRS(t, small, Vector3.one)
                * orientation
                * Matrix4x4.Scale(new Vector3(s, s, s))
                * Matrix4x4.Translate(-objCenter);
        }

        // The 24 proper rotations that map the axes onto the axes (permutations with signs, determinant +1).
        private static Matrix4x4[] BuildOrientations()
        {
            int[][] permutations =
            {
                new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
                new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
            };

            List<Matrix4x4> list = new List<Matrix4x4>();
            foreach (int[] permutation in permutations)
            {
                for (int signs = 0; signs < 8; signs++)
                {
                    Matrix4x4 m = Matrix4x4.identity;
                    for (int column = 0; column < 3; column++)
                    {
                        float sign = ((signs >> column) & 1) == 0 ? 1f : -1f;
                        Vector4 axis = Vector4.zero;
                        axis[permutation[column]] = sign;
                        m.SetColumn(column, axis);
                    }

                    if (m.determinant > 0f)
                    {
                        list.Add(m);
                    }
                }
            }

            return list.ToArray();
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Math.Abs(v.x), Math.Abs(v.y), Math.Abs(v.z));
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
