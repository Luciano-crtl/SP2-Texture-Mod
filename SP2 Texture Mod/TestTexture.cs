using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Phase 2 test image, generated in code (no file needed): an 8x8 checker whose red grows along U
    /// and whose green grows along V, with dark grid lines. Orientation, stretching and seams are obvious.
    /// </summary>
    internal static class TestTexture
    {
        public static Texture2D Create()
        {
            const int size = 512;
            const int cells = 8;
            int cell = size / cells;

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            texture.name = "SP2TextureMod test checker";

            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int cx = x / cell;
                    int cy = y / cell;
                    bool dark = ((cx + cy) & 1) == 0;

                    byte r = (byte)(60 + 195 * cx / (cells - 1));
                    byte g = (byte)(60 + 195 * cy / (cells - 1));
                    byte b = (byte)(dark ? 40 : 200);

                    if ((x % cell) < 3 || (y % cell) < 3)
                    {
                        r = 10;
                        g = 10;
                        b = 10;
                    }

                    pixels[y * size + x] = new Color32(r, g, b, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 8;
            texture.Apply(true, false);
            return texture;
        }
    }
}
