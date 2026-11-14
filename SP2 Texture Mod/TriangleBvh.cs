using System;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Bounding-volume hierarchy over the triangles of an ObjModel, to find the closest point of the
    /// model to a given point. The search can prefer triangles facing the same way as the query:
    /// cost = squared distance + normalWeight * (1 - dot(queryNormal, triangleNormal)).
    /// The extra term is never negative, so boxes can still be skipped using the plain distance.
    /// Not thread safe (one reusable stack).
    /// </summary>
    internal sealed class TriangleBvh
    {
        private const int LeafSize = 4;
        private const int MaxDepth = 48;

        private struct Node
        {
            public Vector3 Min;
            public Vector3 Max;
            public int Left;
            public int Right;
            public int Start;
            public int Count; // > 0 for a leaf
        }

        private readonly ObjModel _model;
        private readonly Vector3[] _normal;
        private readonly Vector3[] _centroid;
        private readonly int[] _order;
        private readonly Node[] _nodes;
        private readonly int[] _stack = new int[256];
        private int _nodeCount;

        public TriangleBvh(ObjModel model)
        {
            _model = model;
            int n = model.TriangleCount;
            _normal = new Vector3[n];
            _centroid = new Vector3[n];
            _order = new int[n];
            _nodes = new Node[Math.Max(1, 2 * n)];

            for (int i = 0; i < n; i++)
            {
                Vector3 a = model.A[i];
                Vector3 b = model.B[i];
                Vector3 c = model.C[i];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float length = cross.magnitude;
                _normal[i] = length > 1e-12f ? cross / length : Vector3.zero;
                _centroid[i] = (a + b + c) * (1f / 3f);
                _order[i] = i;
            }

            if (n > 0)
            {
                Build(0, n, 0);
            }
        }

        private int Build(int start, int count, int depth)
        {
            int index = _nodeCount++;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 cmin = min;
            Vector3 cmax = max;
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                min = Vector3.Min(min, Vector3.Min(_model.A[t], Vector3.Min(_model.B[t], _model.C[t])));
                max = Vector3.Max(max, Vector3.Max(_model.A[t], Vector3.Max(_model.B[t], _model.C[t])));
                cmin = Vector3.Min(cmin, _centroid[t]);
                cmax = Vector3.Max(cmax, _centroid[t]);
            }

            _nodes[index].Min = min;
            _nodes[index].Max = max;

            if (count <= LeafSize)
            {
                _nodes[index].Start = start;
                _nodes[index].Count = count;
                _nodes[index].Left = -1;
                _nodes[index].Right = -1;
                return index;
            }

            Vector3 extent = cmax - cmin;
            int axis = (extent.x >= extent.y && extent.x >= extent.z) ? 0 : (extent.y >= extent.z ? 1 : 2);
            float mid = 0.5f * (cmin[axis] + cmax[axis]);

            int leftCount = count / 2;
            if (depth < MaxDepth)
            {
                // Partition around the middle of the centroid box.
                int i = start;
                int j = start + count - 1;
                while (i <= j)
                {
                    if (_centroid[_order[i]][axis] < mid)
                    {
                        i++;
                    }
                    else
                    {
                        int tmp = _order[i];
                        _order[i] = _order[j];
                        _order[j] = tmp;
                        j--;
                    }
                }

                int partition = i - start;
                if (partition > 0 && partition < count)
                {
                    leftCount = partition;
                }
            }

            int left = Build(start, leftCount, depth + 1);
            int right = Build(start + leftCount, count - leftCount, depth + 1);
            _nodes[index].Left = left;
            _nodes[index].Right = right;
            _nodes[index].Count = 0;
            return index;
        }

        /// <summary>Squared distance from p to the model surface (plain nearest point).</summary>
        public float NearestDistanceSquared(Vector3 p)
        {
            int triangle;
            float u, v, w, distanceSquared;
            if (!Nearest(p, Vector3.zero, 0f, out triangle, out u, out v, out w, out distanceSquared))
            {
                return float.MaxValue;
            }

            return distanceSquared;
        }

        /// <summary>
        /// Finds the best triangle for p. (u, v, w) are the barycentric weights of corners A, B, C
        /// of the closest point. distanceSquared is the plain squared distance to that point.
        /// </summary>
        public bool Nearest(Vector3 p, Vector3 normal, float normalWeight,
            out int triangle, out float u, out float v, out float w, out float distanceSquared)
        {
            triangle = -1;
            u = 0f;
            v = 0f;
            w = 0f;
            distanceSquared = float.MaxValue;
            if (_nodeCount == 0)
            {
                return false;
            }

            bool useNormal = normalWeight > 0f && normal.sqrMagnitude > 0.5f;
            float best = float.MaxValue;

            int sp = 0;
            _stack[sp++] = 0;
            while (sp > 0)
            {
                int ni = _stack[--sp];
                if (BoxDistanceSquared(p, ref _nodes[ni]) >= best)
                {
                    continue;
                }

                if (_nodes[ni].Count > 0)
                {
                    int end = _nodes[ni].Start + _nodes[ni].Count;
                    for (int k = _nodes[ni].Start; k < end; k++)
                    {
                        int t = _order[k];
                        float tu, tv, tw;
                        float d = ClosestPoint(p, _model.A[t], _model.B[t], _model.C[t], out tu, out tv, out tw);
                        float cost = d;
                        if (useNormal)
                        {
                            cost += normalWeight * (1f - Vector3.Dot(normal, _normal[t]));
                        }

                        if (cost < best)
                        {
                            best = cost;
                            triangle = t;
                            u = tu;
                            v = tv;
                            w = tw;
                            distanceSquared = d;
                        }
                    }
                }
                else
                {
                    int left = _nodes[ni].Left;
                    int right = _nodes[ni].Right;
                    float dl = BoxDistanceSquared(p, ref _nodes[left]);
                    float dr = BoxDistanceSquared(p, ref _nodes[right]);

                    // Push the farther child first so the nearer one is visited first.
                    if (dl <= dr)
                    {
                        if (dr < best) _stack[sp++] = right;
                        if (dl < best) _stack[sp++] = left;
                    }
                    else
                    {
                        if (dl < best) _stack[sp++] = left;
                        if (dr < best) _stack[sp++] = right;
                    }
                }
            }

            return triangle >= 0;
        }

        private static float BoxDistanceSquared(Vector3 p, ref Node node)
        {
            float dx = Math.Max(Math.Max(node.Min.x - p.x, 0f), p.x - node.Max.x);
            float dy = Math.Max(Math.Max(node.Min.y - p.y, 0f), p.y - node.Max.y);
            float dz = Math.Max(Math.Max(node.Min.z - p.z, 0f), p.z - node.Max.z);
            return dx * dx + dy * dy + dz * dz;
        }

        // Closest point on triangle abc to p (Ericson, Real-Time Collision Detection). Returns the squared distance.
        private static float ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                u = 1f; v = 0f; w = 0f;
                return (p - a).sqrMagnitude;
            }

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                u = 0f; v = 1f; w = 0f;
                return (p - b).sqrMagnitude;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float t = d1 / (d1 - d3);
                u = 1f - t; v = t; w = 0f;
                return (p - (a + t * ab)).sqrMagnitude;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                u = 0f; v = 0f; w = 1f;
                return (p - c).sqrMagnitude;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float t = d2 / (d2 - d6);
                u = 1f - t; v = 0f; w = t;
                return (p - (a + t * ac)).sqrMagnitude;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                u = 0f; v = 1f - t; w = t;
                return (p - (b + t * (c - b))).sqrMagnitude;
            }

            float sum = va + vb + vc;
            if (Math.Abs(sum) < 1e-20f)
            {
                // Degenerate triangle: fall back to its first corner.
                u = 1f; v = 0f; w = 0f;
                return (p - a).sqrMagnitude;
            }

            float denom = 1f / sum;
            v = vb * denom;
            w = vc * denom;
            u = 1f - v - w;
            return (p - (a + ab * v + ac * w)).sqrMagnitude;
        }
    }
}
