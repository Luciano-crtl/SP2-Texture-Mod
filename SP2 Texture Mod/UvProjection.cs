using System.Collections.Generic;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Writes UV channels of part meshes. Phase 2 uses a simple box projection just to prove the path;
    /// Phase 3 replaces it with the UV transfer from the War Thunder mesh.
    /// </summary>
    internal static class UvProjection
    {
        /// <summary>
        /// The matrix that takes points of a renderer's mesh to the aircraft's own space (the frame the model
        /// alignment is stored in). It is built from the local poses of the transforms between the renderer and
        /// the aircraft, like the game does for flight, so it gives the same answer in the designer and in
        /// flight and does not depend on where the bodies have moved to. If the renderer is not below the
        /// aircraft the world matrices are used instead.
        /// </summary>
        public static Matrix4x4 RendererToCraft(Transform rendererTransform, Transform craftRoot)
        {
            try
            {
                return Jundroo.Common.Utils.UnityTransformUtility.GetTargetToAncestorTransformMatrix(rendererTransform, craftRoot);
            }
            catch (System.Exception)
            {
                // Not below the aircraft (the helper throws), or the helper is missing in this game version.
                return craftRoot.worldToLocalMatrix * rendererTransform.localToWorldMatrix;
            }
        }

        /// <summary>
        /// UV0 from the vertex position in craft space: each vertex is projected along the axis its
        /// normal points to most (X, Y or Z), scaled by tilesPerMeter.
        /// </summary>
        public static void BoxProject(Mesh mesh, Transform rendererTransform, Transform craftRoot, float tilesPerMeter)
        {
            BoxProject(mesh, RendererToCraft(rendererTransform, craftRoot), tilesPerMeter);
        }

        /// <summary>The same, with the mesh-to-craft matrix already worked out.</summary>
        public static void BoxProject(Mesh mesh, Matrix4x4 toCraft, float tilesPerMeter)
        {
            int count = mesh.vertexCount;

            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = toCraft.MultiplyPoint3x4(vertices[i]);
                Vector3 n = hasNormals ? toCraft.MultiplyVector(normals[i]) : Vector3.up;

                float ax = Mathf.Abs(n.x);
                float ay = Mathf.Abs(n.y);
                float az = Mathf.Abs(n.z);

                Vector2 uv;
                if (ax >= ay && ax >= az)
                {
                    uv = new Vector2(p.z, p.y);
                }
                else if (ay >= az)
                {
                    uv = new Vector2(p.x, p.z);
                }
                else
                {
                    uv = new Vector2(p.x, p.y);
                }

                uvs.Add(uv * tilesPerMeter);
            }

            mesh.SetUVs(0, uvs);
        }

        /// <summary>Puts every UV0 back to zero, which is what procedural part meshes have by default.</summary>
        public static void ClearUv0(Mesh mesh)
        {
            mesh.SetUVs(0, new List<Vector2>(new Vector2[mesh.vertexCount]));
        }

        /// <summary>
        /// UV1 is (materialIndex, decalLayer, partId) per vertex. Only x is changed here, so decals
        /// and the part id keep working.
        /// </summary>
        public static bool SetMaterialIndex(Mesh mesh, int materialIndex)
        {
            int count = mesh.vertexCount;
            List<Vector3> uv1 = new List<Vector3>(count);
            mesh.GetUVs(1, uv1);
            if (uv1.Count != count)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 v = uv1[i];
                v.x = materialIndex;
                uv1[i] = v;
            }

            mesh.SetUVs(1, uv1);
            return true;
        }
    }
}
