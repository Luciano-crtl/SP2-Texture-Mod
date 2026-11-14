using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Writes UV0 of a part mesh by copying the UV of the closest point of the War Thunder model.
    /// Each vertex is looked up independently; the search prefers model triangles that face the same way
    /// as the vertex normal, so the top of a wing does not pick the bottom of the model.
    /// </summary>
    internal static class UvTransfer
    {
        internal sealed class Stats
        {
            public long Vertices;
            public long Far;
            public double SumDistance;
            public float MaxDistance;

            public void Reset()
            {
                Vertices = 0;
                Far = 0;
                SumDistance = 0;
                MaxDistance = 0f;
            }

            public string Describe(float farLimit)
            {
                if (Vertices == 0)
                {
                    return "no vertices";
                }

                return "vertices=" + Vertices
                    + " meanDistance=" + (SumDistance / Vertices).ToString("0.###", CultureInfo.InvariantCulture) + " m"
                    + " maxDistance=" + MaxDistance.ToString("0.###", CultureInfo.InvariantCulture) + " m"
                    + " farther than " + farLimit.ToString("0.##", CultureInfo.InvariantCulture) + " m: " + Far
                    + " (" + (100.0 * Far / Vertices).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
            }
        }

        public static void Write(Mesh mesh, Transform rendererTransform, Transform craftRoot,
            ObjModel model, TriangleBvh bvh, ModelAlignment alignment,
            float normalWeightSquareMeters, float farLimitMeters, Stats stats)
        {
            int count = mesh.vertexCount;

            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            Matrix4x4 toCraft = craftRoot.worldToLocalMatrix * rendererTransform.localToWorldMatrix;
            Matrix4x4 craftToObj = alignment.CraftToObj;
            float scale = Mathf.Max(alignment.Scale, 1e-6f);
            float weightInObjUnits = normalWeightSquareMeters / (scale * scale);

            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = craftToObj.MultiplyPoint3x4(toCraft.MultiplyPoint3x4(vertices[i]));
                Vector3 n = Vector3.zero;
                if (hasNormals)
                {
                    n = craftToObj.MultiplyVector(toCraft.MultiplyVector(normals[i])).normalized;
                }

                int triangle;
                float u, v, w, distanceSquared;
                if (bvh.Nearest(p, n, weightInObjUnits, out triangle, out u, out v, out w, out distanceSquared))
                {
                    uvs.Add(u * model.UvA[triangle] + v * model.UvB[triangle] + w * model.UvC[triangle]);

                    float meters = Mathf.Sqrt(distanceSquared) * scale;
                    stats.Vertices++;
                    stats.SumDistance += meters;
                    if (meters > stats.MaxDistance)
                    {
                        stats.MaxDistance = meters;
                    }

                    if (meters > farLimitMeters)
                    {
                        stats.Far++;
                    }
                }
                else
                {
                    uvs.Add(Vector2.zero);
                }
            }

            mesh.SetUVs(0, uvs);
        }
    }
}
