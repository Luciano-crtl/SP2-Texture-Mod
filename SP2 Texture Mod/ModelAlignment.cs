using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Finds the transform that puts a War Thunder OBJ model on top of the craft the user built, with no
    /// manual work. For each of the 24 axis-aligned orientations the model is scaled to the craft's bounds;
    /// the best few are refined (position, uniform scale, small rotations; coordinate descent) and returned
    /// sorted, so the user can step to the next one when the best is not the right one.
    /// The quality measure is the mean distance from sample points of the craft's surface to the model's
    /// surface. Only the worst 2% are ignored: a thin, symmetric aircraft fits upside-down almost as well
    /// as the right way up, and what tells them apart (the canopy, the belly) must not be trimmed away.
    /// Orientations that keep the model's up axis up get a small bonus, since models and crafts are
    /// practically never upside-down or on their side.
    /// </summary>
    internal sealed class ModelAlignment
    {
        public Matrix4x4 ObjToCraft;
        public Matrix4x4 CraftToObj;

        /// <summary>Meters of craft per unit of the OBJ.</summary>
        public float Scale;

        /// <summary>Mean distance in meters after fitting (only the worst 2% left out).</summary>
        public float MeanDistance;

        /// <summary>MeanDistance with the up-axis bonus applied: what the candidates are sorted by.</summary>
        public float Score;

        public string Summary;

        private const float KeepFraction = 0.98f;
        private const int RefineCount = 3;
        private const float UpBonus = 0.9f;

        private static readonly Matrix4x4[] Orientations = BuildOrientations();

        // What a evaluation needs: the model's index, the craft's samples, and (for the two-sided measure) a grid of many craft
        // points and a sample of the model's surface.
        private sealed class Context
        {
            public TriangleBvh Bvh;
            public List<Vector3> Samples;
            public PointGrid Grid;       // null = one-sided measure only
            public Vector3[] ModelPoints;
            public float[] Scratch;
            public float[] ScratchB;
            public float LastA; // the one-sided part of the last evaluation
        }

        // Craft points kept in a uniform grid, to find the nearest one quickly.
        private sealed class PointGrid
        {
            private readonly Vector3[] _points;
            private readonly int[] _start;
            private readonly Vector3 _min;
            private readonly float _cell;
            private readonly int _nx, _ny, _nz;
            private readonly Vector3 _max;

            public PointGrid(List<Vector3> points, Bounds bounds)
            {
                _min = bounds.min;
                _max = bounds.max;
                Vector3 size = bounds.size;
                _cell = Math.Max(1e-3f, Math.Max(size.x, Math.Max(size.y, size.z)) / 32f);
                _nx = Math.Max(1, (int)(size.x / _cell) + 1);
                _ny = Math.Max(1, (int)(size.y / _cell) + 1);
                _nz = Math.Max(1, (int)(size.z / _cell) + 1);
                int cells = _nx * _ny * _nz;
                int[] counts = new int[cells + 1];
                int[] cellOf = new int[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    cellOf[i] = CellIndex(points[i]);
                    counts[cellOf[i] + 1]++;
                }

                for (int c = 0; c < cells; c++)
                {
                    counts[c + 1] += counts[c];
                }

                _start = counts;
                _points = new Vector3[points.Count];
                int[] fill = new int[cells];
                for (int i = 0; i < points.Count; i++)
                {
                    _points[_start[cellOf[i]] + fill[cellOf[i]]++] = points[i];
                }
            }

            private int Clamp(int v, int n)
            {
                return v < 0 ? 0 : (v >= n ? n - 1 : v);
            }

            private int CellIndex(Vector3 p)
            {
                int x = Clamp((int)((p.x - _min.x) / _cell), _nx);
                int y = Clamp((int)((p.y - _min.y) / _cell), _ny);
                int z = Clamp((int)((p.z - _min.z) / _cell), _nz);
                return (z * _ny + y) * _nx + x;
            }

            /// <summary>Distance to the nearest point (at most a few cells away is searched; farther counts as that).</summary>
            public float Nearest(Vector3 p)
            {
                // A point outside the grid is at least as far as the box.
                float ox = Math.Max(Math.Max(_min.x - p.x, p.x - _max.x), 0f);
                float oy = Math.Max(Math.Max(_min.y - p.y, p.y - _max.y), 0f);
                float oz = Math.Max(Math.Max(_min.z - p.z, p.z - _max.z), 0f);
                float outside = Mathf.Sqrt(ox * ox + oy * oy + oz * oz);

                int cx = Clamp((int)((p.x - _min.x) / _cell), _nx);
                int cy = Clamp((int)((p.y - _min.y) / _cell), _ny);
                int cz = Clamp((int)((p.z - _min.z) / _cell), _nz);
                float best = float.MaxValue;
                const int MaxRing = 5;
                for (int r = 0; r <= MaxRing; r++)
                {
                    for (int z = cz - r; z <= cz + r; z++)
                    {
                        if (z < 0 || z >= _nz) continue;
                        for (int y = cy - r; y <= cy + r; y++)
                        {
                            if (y < 0 || y >= _ny) continue;
                            bool edgeYZ = z == cz - r || z == cz + r || y == cy - r || y == cy + r;
                            for (int x = cx - r; x <= cx + r; x += (edgeYZ || r == 0) ? 1 : 2 * r)
                            {
                                if (x < 0 || x >= _nx) continue;
                                int cell = (z * _ny + y) * _nx + x;
                                for (int i = _start[cell]; i < _start[cell + 1]; i++)
                                {
                                    float dx = _points[i].x - p.x, dy = _points[i].y - p.y, dz = _points[i].z - p.z;
                                    float d = dx * dx + dy * dy + dz * dz;
                                    if (d < best) best = d;
                                }
                            }
                        }
                    }

                    if (best < float.MaxValue && Mathf.Sqrt(best) <= r * _cell)
                    {
                        break;
                    }
                }

                float result = best < float.MaxValue ? Mathf.Sqrt(best) : (MaxRing + 1) * _cell;
                return Math.Max(result, outside);
            }
        }

        /// <summary>
        /// gridPoints: many points of the craft's surface (in craft space). With them the fit also measures how far the model's
        /// surface is from the craft (the worst 30% ignored: parts the craft does not have), so a model that is a little too big
        /// no longer fits as well as the right size (the one-sided measure alone rewards a large model, which has surface
        /// near every point of the craft, inside the craft too).
        /// </summary>
        public static List<ModelAlignment> FitCandidates(ObjModel model, TriangleBvh bvh, List<Vector3> samples, Bounds craftBounds,
            List<Vector3> gridPoints = null)
        {
            Vector3 objCenter = model.Bounds.center;
            Vector3 objSize = model.Bounds.size;
            Vector3 craftCenter = craftBounds.center;
            Vector3 craftSize = craftBounds.size;
            float craftMax = Math.Max(craftSize.x, Math.Max(craftSize.y, craftSize.z));
            Context context = new Context();
            context.Bvh = bvh;
            context.Samples = samples;
            context.Scratch = new float[samples.Count];
            if (gridPoints != null && gridPoints.Count > 50)
            {
                Bounds grown = craftBounds;
                grown.Expand(0.01f);
                context.Grid = new PointGrid(gridPoints, grown);
                int wanted = Math.Min(600, model.TriangleCount);
                int step = Math.Max(1, model.TriangleCount / wanted);
                List<Vector3> picked = new List<Vector3>(wanted);
                for (int t = 0; t < model.TriangleCount && picked.Count < wanted; t += step)
                {
                    picked.Add((model.A[t] + model.B[t] + model.C[t]) * (1f / 3f));
                }

                context.ModelPoints = picked.ToArray();
                context.ScratchB = new float[picked.Count];
            }

            // 1. First guess of every orientation: bounding boxes of similar size, centered on each other.
            int count = Orientations.Length;
            float[][] starts = new float[count][];
            float[] firstScore = new float[count];
            float[] firstMean = new float[count];
            int[] order = new int[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 rotated = Abs(Orientations[k].MultiplyVector(objSize));
                float sx = craftSize.x / Math.Max(rotated.x, 1e-4f);
                float sy = craftSize.y / Math.Max(rotated.y, 1e-4f);
                float sz = craftSize.z / Math.Max(rotated.z, 1e-4f);
                float s0 = Mathf.Pow(Math.Max(sx * sy * sz, 1e-9f), 1f / 3f);

                float[] x = { craftCenter.x, craftCenter.y, craftCenter.z, Mathf.Log(s0), 0f, 0f, 0f };
                starts[k] = x;
                firstMean[k] = Evaluate(context, Build(Orientations[k], objCenter, x), Mathf.Exp(x[3]));
                firstScore[k] = firstMean[k] * Bonus(k);
                order[k] = k;
            }

            float[] keys = (float[])firstScore.Clone();
            Array.Sort(keys, order);

            // 2. Refine the best ones.
            List<ModelAlignment> result = new List<ModelAlignment>();
            int refine = Math.Min(RefineCount, count);
            for (int r = 0; r < refine; r++)
            {
                int k = order[r];
                float[] p = (float[])starts[k].Clone();
                Refine(context, Orientations[k], objCenter, p, craftMax);
                Evaluate(context, Build(Orientations[k], objCenter, p), Mathf.Exp(p[3]));
                float mean = context.LastA; // reported (and used for the "poor fit" warning) as the distance from the craft to the model

                ModelAlignment candidate = new ModelAlignment();
                candidate.ObjToCraft = Build(Orientations[k], objCenter, p);
                candidate.CraftToObj = candidate.ObjToCraft.inverse;
                candidate.Scale = Mathf.Exp(p[3]);
                candidate.MeanDistance = mean;
                candidate.Score = mean * Bonus(k);
                candidate.Summary = "orientation #" + k + " (" + (Bonus(k) < 1f ? "keeps up" : "tilted") + ") scale " + F(candidate.Scale)
                    + " | mean distance " + F(mean) + " m (" + F(100f * mean / Math.Max(craftMax, 1e-4f)) + "% of the craft's largest size, "
                    + samples.Count + " samples)";
                result.Add(candidate);
            }

            result.Sort((a, b) => a.Score.CompareTo(b.Score));
            return result;
        }

        /// <summary>An alignment taken from a model-to-craft matrix that was stored earlier (no search is done).</summary>
        public static ModelAlignment FromMatrix(Matrix4x4 objToCraft)
        {
            ModelAlignment result = new ModelAlignment();
            result.ObjToCraft = objToCraft;
            result.CraftToObj = objToCraft.inverse;
            result.Scale = objToCraft.MultiplyVector(Vector3.right).magnitude;
            result.MeanDistance = 0f;
            result.Score = 0f;
            result.Summary = "stored alignment";
            return result;
        }

        /// <summary>
        /// Applies the manual correction from the settings on top of an automatic fit: rotation and scale
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
            result.Score = Score;
            result.Summary = Summary;
            return result;
        }

        // Coordinate descent on position (3), log scale (1) and small rotations (3). Returns the mean distance.
        private static float Refine(Context context, Matrix4x4 orientation, Vector3 objCenter, float[] p, float craftMax)
        {
            float cur = Evaluate(context, Build(orientation, objCenter, p), Mathf.Exp(p[3]));
            float[] step = { 0.05f * craftMax, 0.05f * craftMax, 0.05f * craftMax, 0.04f, 3f, 3f, 3f };
            for (int round = 0; round < 8; round++)
            {
                for (int i = 0; i < 7; i++)
                {
                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        float old = p[i];
                        p[i] = old + dir * step[i];
                        float c = Evaluate(context, Build(orientation, objCenter, p), Mathf.Exp(p[3]));
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

            return cur;
        }

        // The cost of an alignment: the mean distance from the craft's samples to the model (the worst 2% left out) plus, when
        // there is a grid, the mean distance from the model's surface sample to the craft (the worst 30% left out).
        private static float Evaluate(Context c, Matrix4x4 objToCraft, float scale)
        {
            Matrix4x4 inverse = objToCraft.inverse;
            List<Vector3> samples = c.Samples;
            int n = samples.Count;
            float[] scratch = c.Scratch;
            for (int i = 0; i < n; i++)
            {
                Vector3 q = inverse.MultiplyPoint3x4(samples[i]);
                scratch[i] = Mathf.Sqrt(c.Bvh.NearestDistanceSquared(q)) * scale;
            }

            Array.Sort(scratch, 0, n);
            int keep = Math.Max(1, (int)(n * KeepFraction));
            float sum = 0f;
            for (int i = 0; i < keep; i++)
            {
                sum += scratch[i];
            }

            c.LastA = sum / keep;
            if (c.Grid == null)
            {
                return c.LastA;
            }

            int m = c.ModelPoints.Length;
            for (int i = 0; i < m; i++)
            {
                c.ScratchB[i] = c.Grid.Nearest(objToCraft.MultiplyPoint3x4(c.ModelPoints[i]));
            }

            Array.Sort(c.ScratchB, 0, m);
            int keepB = Math.Max(1, (int)(m * 0.7f));
            float sumB = 0f;
            for (int i = 0; i < keepB; i++)
            {
                sumB += c.ScratchB[i];
            }

            return c.LastA + sumB / keepB;
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

        private static float Bonus(int orientationIndex)
        {
            return Orientations[orientationIndex].MultiplyVector(Vector3.up).y > 0.99f ? UpBonus : 1f;
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
