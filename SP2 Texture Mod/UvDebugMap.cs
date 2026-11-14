using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Writes a picture of where the vertices of the craft landed on the texture, to see at a glance whether
    /// the match is right: the texture darkened, with one dot per vertex. Red dots are vertices that face up,
    /// blue ones face down, green ones face sideways, yellow ones were found by the nearest-surface fallback.
    /// A wing top should land on the upper-wing islands of the texture and the belly on the underside islands.
    /// The file is called debug_uv_map.png and is ignored by the image list.
    /// </summary>
    internal static class UvDebugMap
    {
        public const string FileName = "debug_uv_map.png";

        public static string Write(Texture2D atlas, List<UvTransfer.DebugPoint> points)
        {
            const int size = 1024;

            Color32[] source = atlas.GetPixels32();
            int sw = atlas.width;
            int sh = atlas.height;

            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                int sy = Math.Min(sh - 1, y * sh / size);
                for (int x = 0; x < size; x++)
                {
                    Color32 c = source[sy * sw + Math.Min(sw - 1, x * sw / size)];
                    pixels[y * size + x] = new Color32((byte)(c.r * 55 / 100), (byte)(c.g * 55 / 100), (byte)(c.b * 55 / 100), 255);
                }
            }

            for (int i = 0; i < points.Count; i++)
            {
                UvTransfer.DebugPoint point = points[i];
                Color32 color = point.Fallback
                    ? new Color32(255, 230, 0, 255)
                    : (point.Kind == 0 ? new Color32(255, 40, 40, 255)
                        : (point.Kind == 1 ? new Color32(60, 130, 255, 255) : new Color32(60, 230, 60, 255)));

                // Texture coordinates have their origin at the bottom left, like the rows of the pixel array.
                int px = Mathf.RoundToInt(point.Uv.x * (size - 1));
                int py = Mathf.RoundToInt(point.Uv.y * (size - 1));
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = px + dx;
                        int y = py + dy;
                        if (x >= 0 && x < size && y >= 0 && y < size)
                        {
                            pixels[y * size + x] = color;
                        }
                    }
                }
            }

            Texture2D map = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                map.SetPixels32(pixels);
                map.Apply(false, false);
                string path = Path.Combine(TextureLibrary.Folder, FileName);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(map));
                return path;
            }
            finally
            {
                UnityEngine.Object.Destroy(map);
            }
        }
    }
}
