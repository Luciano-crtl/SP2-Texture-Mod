using System;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Finds a place in the atlas with an average color, for the vertices that have no counterpart on the model (see
    /// UvTransfer.Options.NeutralUv): a sample of the UVs the model really uses is read from a small mip level of the image,
    /// and the sample whose color is closest to the average is taken.
    /// </summary>
    internal static class UvNeutral
    {
        public static bool Find(Texture2D texture, ObjModel model, out Vector2 uv)
        {
            uv = new Vector2(0.5f, 0.5f);
            try
            {
                int level = Math.Max(0, Math.Min(6, texture.mipmapCount - 1));
                int width = Math.Max(1, texture.width >> level);
                int height = Math.Max(1, texture.height >> level);
                Color32[] pixels = texture.GetPixels32(level);
                if (pixels == null || pixels.Length < width * height || model.TriangleCount == 0)
                {
                    return false;
                }

                int count = Math.Min(4000, model.TriangleCount);
                int step = Math.Max(1, model.TriangleCount / count);
                Vector2[] points = new Vector2[count];
                Vector3[] colors = new Vector3[count];
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int t = 0; t < model.TriangleCount && n < count; t += step)
                {
                    Vector2 c = (model.UvA[t] + model.UvB[t] + model.UvC[t]) * (1f / 3f);
                    float fu = c.x - (float)Math.Floor(c.x);
                    float fv = c.y - (float)Math.Floor(c.y);
                    int x = Math.Min(width - 1, (int)(fu * width));
                    int y = Math.Min(height - 1, (int)(fv * height));
                    Color32 p = pixels[y * width + x];
                    points[n] = new Vector2(fu, fv);
                    colors[n] = new Vector3(p.r, p.g, p.b);
                    sum += colors[n];
                    n++;
                }

                if (n == 0)
                {
                    return false;
                }

                Vector3 mean = sum * (1f / n);
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    float d = (colors[i] - mean).sqrMagnitude;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }

                uv = points[best];
                return true;
            }
            catch (Exception)
            {
                return false; // an image that cannot be read back: no neutral color
            }
        }
    }
}
