using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Writes UV0 of a part mesh by copying the UV of the War Thunder model at the matching point.
    /// Two ways to find it:
    ///  - project: a ray starts a little outside the vertex and goes inwards along the vertex normal; the first
    ///    model surface it meets is the match. This is how textures are baked from a detailed model onto a
    ///    simple one: it never lands on the other skin of a thin wing or on parts inside the fuselage, and it
    ///    does not depend on how the model's faces wind when frontFacesOnly is off;
    ///  - nearest: the closest model surface (with a small preference for surfaces facing the same way).
    /// The projection falls back to the nearest surface where the ray misses (silhouettes, edges).
    /// </summary>
    internal static class UvTransfer
    {
        internal sealed class Options
        {
            /// <summary>true = project along the normal, false = nearest surface.</summary>
            public bool Project;

            /// <summary>How far outside the vertex the ray starts (meters).</summary>
            public float RayStartMeters;

            /// <summary>How far past the vertex a hit is still accepted (meters).</summary>
            public float MaxDistanceMeters;

            /// <summary>Only model faces that face the ray's origin count (needs a model whose faces wind consistently).</summary>
            public bool FrontFacesOnly;

            /// <summary>Preference for same-facing surfaces in the nearest search (square meters).</summary>
            public float FacingWeightSquareMeters;

            /// <summary>Distance beyond which a match is counted as "far" in the statistics (meters).</summary>
            public float FarLimitMeters;

            /// <summary>
            /// A vertex whose match is farther than this from it (meters) has no counterpart on the model (a part the model
            /// does not have, or has elsewhere): it gets NeutralUv instead of the coordinates of whatever surface was nearest.
            /// 0 = off.
            /// </summary>
            public float NeutralBeyondMeters;

            /// <summary>A place of the atlas with an average color.</summary>
            public Vector2 NeutralUv;

            /// <summary>
            /// Largest shift (meters) the correction of a single renderer may have (see PartRefinement). 0 = every renderer
            /// uses the alignment of the whole craft as it is.
            /// </summary>
            public float PartRefineMeters;
        }

        internal struct DebugPoint
        {
            public Vector2 Uv;
            public byte Kind; // 0 = faces up, 1 = faces down, 2 = faces sideways
            public bool Fallback;
        }

        internal sealed class Stats
        {
            public long Vertices;
            public long Rays;
            public long Far;
            public long Neutral;
            public long RefinedRenderers;
            public long Renderers;
            public double SumRefineShift;
            public double SumBeforeRefine;
            public double SumAfterRefine;
            public double SumDistance;
            public float MaxDistance;
            public readonly long[] Count = new long[3];
            public readonly long[] Agree = new long[3];
            public readonly double[] SumHeight = new double[3];
            public readonly List<DebugPoint> Points = new List<DebugPoint>();
            public float MinU = float.MaxValue;
            public float MaxU = float.MinValue;
            public float MinV = float.MaxValue;
            public float MaxV = float.MinValue;

            private const int MaxPoints = 400000;

            public void Reset()
            {
                Vertices = 0;
                Rays = 0;
                Far = 0;
                Neutral = 0;
                RefinedRenderers = 0;
                Renderers = 0;
                SumRefineShift = 0;
                SumBeforeRefine = 0;
                SumAfterRefine = 0;
                SumDistance = 0;
                MaxDistance = 0f;
                for (int i = 0; i < 3; i++)
                {
                    Count[i] = 0;
                    Agree[i] = 0;
                    SumHeight[i] = 0;
                }

                Points.Clear();
                MinU = float.MaxValue;
                MaxU = float.MinValue;
                MinV = float.MaxValue;
                MaxV = float.MinValue;
            }

            public void TrackUv(Vector2 uv)
            {
                if (uv.x < MinU) MinU = uv.x;
                if (uv.x > MaxU) MaxU = uv.x;
                if (uv.y < MinV) MinV = uv.y;
                if (uv.y > MaxV) MaxV = uv.y;
            }

            /// <summary>"by ray 86%, mean 0.10 m": the short form for the panel.</summary>
            public string MatchSummary()
            {
                if (Vertices == 0)
                {
                    return "-";
                }

                return "ray " + F(100.0 * Rays / Vertices) + "%, mean " + F(SumDistance / Vertices) + " m, max " + F(MaxDistance) + " m";
            }

            /// <summary>"120 of 240 renderers moved, mean shift 0.21 m, distance 0.62 -> 0.31 m".</summary>
            public string RefineSummary()
            {
                if (Renderers == 0)
                {
                    return "-";
                }

                if (RefinedRenderers == 0)
                {
                    return "none of " + Renderers + " renderers needed a correction";
                }

                return RefinedRenderers + " of " + Renderers + " renderers moved, mean shift " + F(SumRefineShift / RefinedRenderers)
                    + " m, distance of those " + F(SumBeforeRefine / RefinedRenderers) + " -> " + F(SumAfterRefine / RefinedRenderers) + " m";
            }

            public string UvRangeSummary()
            {
                return Vertices == 0 ? "-" : "u " + F(MinU) + ".." + F(MaxU) + ", v " + F(MinV) + ".." + F(MaxV);
            }

            /// <summary>"5440, agree 77%": how many vertices of a kind (0 up, 1 down, 2 sideways) and how often the matched face points the same way.</summary>
            public string KindSummary(int kind)
            {
                if (Count[kind] == 0)
                {
                    return "0";
                }

                return Count[kind] + ", agree " + F(100.0 * Agree[kind] / Count[kind]) + "%";
            }

            public void AddPoint(Vector2 uv, int kind, bool fallback)
            {
                if (Points.Count < MaxPoints)
                {
                    DebugPoint point = new DebugPoint();
                    point.Uv = uv;
                    point.Kind = (byte)kind;
                    point.Fallback = fallback;
                    Points.Add(point);
                }
            }

            public string Describe(float farLimit)
            {
                if (Vertices == 0)
                {
                    return "no vertices";
                }

                return "vertices=" + Vertices
                    + ", by ray " + F(100.0 * Rays / Vertices) + "%"
                    + " | distance mean " + F(SumDistance / Vertices) + " m, max " + F(MaxDistance) + " m, over " + F(farLimit) + " m: " + Far
                    + " | faces up: " + DescribeKind(0) + " | faces down: " + DescribeKind(1) + " | sideways: " + DescribeKind(2);
            }

            // For a kind of vertex: how many, how often the matched model face points the same way, and how far
            // above (+) or below (-) the vertex the matched point is on average (a top that matches below is wrong).
            private string DescribeKind(int kind)
            {
                if (Count[kind] == 0)
                {
                    return "0";
                }

                return Count[kind] + " (agree " + F(100.0 * Agree[kind] / Count[kind]) + "%, match "
                    + F(SumHeight[kind] / Count[kind]) + " m high)";
            }

            private static string F(double value)
            {
                return value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        public static void Write(Mesh mesh, Transform rendererTransform, Transform craftRoot,
            ObjModel model, TriangleBvh bvh, ModelAlignment alignment, Options options, Stats stats)
        {
            Write(mesh, UvProjection.RendererToCraft(rendererTransform, craftRoot), model, bvh, alignment, options, stats);
        }

        /// <summary>The same, with the mesh-to-craft matrix already worked out.</summary>
        public static void Write(Mesh mesh, Matrix4x4 toCraft,
            ObjModel model, TriangleBvh bvh, ModelAlignment alignment, Options options, Stats stats)
        {
            int count = mesh.vertexCount;

            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            Matrix4x4 craftToObj = alignment.CraftToObj;
            Matrix4x4 objToCraft = alignment.ObjToCraft;
            float scale = Mathf.Max(alignment.Scale, 1e-6f);
            float rayStartObj = options.RayStartMeters / scale;
            float rayRangeObj = (options.RayStartMeters + options.MaxDistanceMeters) / scale;
            float weightObj = options.FacingWeightSquareMeters / (scale * scale);

            // The craft-space position of every vertex, then the correction of this renderer (if enabled).
            List<Vector3> craftVertices = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                craftVertices.Add(toCraft.MultiplyPoint3x4(vertices[i]));
            }

            PartRefinement.Correction correction = PartRefinement.Correction.None;
            if (options.PartRefineMeters > 0f)
            {
                correction = PartRefinement.Fit(craftVertices, bvh, alignment, options.PartRefineMeters);
                stats.Renderers++;
                if (correction.Moved)
                {
                    stats.RefinedRenderers++;
                    stats.SumRefineShift += correction.Shift.magnitude;
                    stats.SumBeforeRefine += correction.Before;
                    stats.SumAfterRefine += correction.After;
                }
            }

            bool corrected = correction.Moved;

            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 pCraft = craftVertices[i];
                Vector3 nCraft = hasNormals ? toCraft.MultiplyVector(normals[i]).normalized : Vector3.zero;
                if (corrected)
                {
                    pCraft = correction.Apply(pCraft);
                    if (hasNormals)
                    {
                        nCraft = correction.ApplyNormal(nCraft);
                    }
                }

                Vector3 p = craftToObj.MultiplyPoint3x4(pCraft);
                Vector3 n = hasNormals ? craftToObj.MultiplyVector(nCraft).normalized : Vector3.zero;

                int triangle = -1;
                float u = 0f, v = 0f, w = 0f;
                bool byRay = false;
                bool matched = false;

                if (options.Project && hasNormals)
                {
                    float hit;
                    matched = bvh.Raycast(p + n * rayStartObj, -n, rayRangeObj, options.FrontFacesOnly,
                        out triangle, out u, out v, out w, out hit);
                    byRay = matched;
                }

                if (!matched)
                {
                    float distanceSquared;
                    matched = bvh.Nearest(p, n, weightObj, out triangle, out u, out v, out w, out distanceSquared);
                }

                if (!matched)
                {
                    uvs.Add(Vector2.zero);
                    continue;
                }

                Vector2 uv = u * model.UvA[triangle] + v * model.UvB[triangle] + w * model.UvC[triangle];

                // Where the match landed relative to the vertex.
                Vector3 matchObj = u * model.A[triangle] + v * model.B[triangle] + w * model.C[triangle];
                Vector3 matchCraft = objToCraft.MultiplyPoint3x4(matchObj);
                float meters = (matchCraft - pCraft).magnitude;
                if (options.NeutralBeyondMeters > 0f && meters > options.NeutralBeyondMeters)
                {
                    uv = options.NeutralUv;
                    stats.Neutral++;
                }
                else
                {
                    stats.TrackUv(uv);
                }

                uvs.Add(uv);
                int kind = nCraft.y > 0.6f ? 0 : (nCraft.y < -0.6f ? 1 : 2);

                stats.Vertices++;
                if (byRay)
                {
                    stats.Rays++;
                }

                stats.SumDistance += meters;
                if (meters > stats.MaxDistance)
                {
                    stats.MaxDistance = meters;
                }

                if (meters > options.FarLimitMeters)
                {
                    stats.Far++;
                }

                stats.Count[kind]++;
                stats.SumHeight[kind] += matchCraft.y - pCraft.y;
                if (hasNormals && Vector3.Dot(objToCraft.MultiplyVector(bvh.NormalOf(triangle)).normalized, nCraft) > 0f)
                {
                    stats.Agree[kind]++;
                }

                stats.AddPoint(uv, kind, !byRay);
            }

            mesh.SetUVs(0, uvs);
        }
    }
}
