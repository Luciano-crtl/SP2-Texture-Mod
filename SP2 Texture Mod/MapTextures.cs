using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Reads the extra maps of a material (normal, occlusion, height) and prepares them for the part shader.
    /// These are data, not colors, so they are made as linear textures (no sRGB decoding), and the pixels are
    /// cleaned up on the way: a normal map gets an opaque alpha (the shader's unpacking multiplies by it) and,
    /// optionally, a green flip (DirectX-style maps point green down) and a rebuilt blue (many game textures
    /// keep other data there). A grayscale image given as a normal map is taken to be a bump map and converted.
    /// </summary>
    internal static class MapTextures
    {
        public static Texture2D LoadNormal(string path, bool flipGreen, bool rebuildZ, float bumpStrength, out string note)
        {
            int width;
            int height;
            Color32[] pixels = ReadPixels(path, out width, out height);
            if (pixels == null)
            {
                note = "could not read the image";
                return null;
            }

            if (IsGrayscale(pixels))
            {
                note = "grayscale image: converted from a bump map (strength " + bumpStrength.ToString("0.#", CultureInfo.InvariantCulture) + ")";
                return MakeLinear(Path.GetFileName(path), width, height, NormalFromHeight(pixels, width, height, bumpStrength));
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                byte g = flipGreen ? (byte)(255 - c.g) : c.g;
                byte b = c.b;
                if (rebuildZ)
                {
                    float x = c.r / 127.5f - 1f;
                    float y = g / 127.5f - 1f;
                    float zz = 1f - x * x - y * y;
                    float z = zz > 0f ? Mathf.Sqrt(zz) : 0f;
                    b = (byte)Mathf.Clamp(Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 0, 255);
                }

                pixels[i] = new Color32(c.r, g, b, 255);
            }

            note = (flipGreen ? "green flipped" : "green as it is") + (rebuildZ ? ", blue rebuilt" : "");
            return MakeLinear(Path.GetFileName(path), width, height, pixels);
        }

        /// <summary>A normal map made from a grayscale height (bump) image.</summary>
        public static Texture2D LoadNormalFromHeight(string path, float bumpStrength, out string note)
        {
            int width;
            int height;
            Color32[] pixels = ReadPixels(path, out width, out height);
            if (pixels == null)
            {
                note = "could not read the image";
                return null;
            }

            note = "made from the height map (strength " + bumpStrength.ToString("0.#", CultureInfo.InvariantCulture) + ")";
            return MakeLinear(Path.GetFileName(path) + " (as normal)", width, height, NormalFromHeight(pixels, width, height, bumpStrength));
        }

        /// <summary>Occlusion: the shader reads the green channel; a gray image is used as it is, otherwise the red channel.</summary>
        public static Texture2D LoadOcclusion(string path, out string note)
        {
            int width;
            int height;
            Color32[] pixels = ReadPixels(path, out width, out height);
            if (pixels == null)
            {
                note = "could not read the image";
                return null;
            }

            bool gray = IsGrayscale(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = gray ? pixels[i].g : pixels[i].r;
                pixels[i] = new Color32(v, v, v, 255);
            }

            note = gray ? "grayscale" : "not grayscale: its red channel is used";
            return MakeLinear(Path.GetFileName(path), width, height, pixels);
        }

        /// <summary>Height for the parallax effect: the shader reads the green channel; the image is made gray.</summary>
        public static Texture2D LoadHeight(string path, out string note)
        {
            int width;
            int height;
            Color32[] pixels = ReadPixels(path, out width, out height);
            if (pixels == null)
            {
                note = "could not read the image";
                return null;
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = (byte)((pixels[i].r + pixels[i].g + pixels[i].b) / 3);
                pixels[i] = new Color32(v, v, v, 255);
            }

            note = "made gray";
            return MakeLinear(Path.GetFileName(path), width, height, pixels);
        }

        /// <summary>
        /// The part shader cannot read a specular map, so the image is only measured: the average brightness
        /// (0 = black, 1 = white) and the share of clearly bright pixels. The average is what the mod uses.
        /// </summary>
        public static bool AnalyzeSpecular(string path, out float average, out float brightShare, out string note)
        {
            average = 0f;
            brightShare = 0f;

            int width;
            int height;
            Color32[] pixels = ReadPixels(path, out width, out height);
            if (pixels == null)
            {
                note = "could not read the image";
                return false;
            }

            // About 200 thousand samples are plenty; an odd step does not line up with the rows.
            int step = Math.Max(1, pixels.Length / 200000) | 1;
            double sum = 0.0;
            int bright = 0;
            int total = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                Color32 c = pixels[i];
                float value = (c.r + c.g + c.b) / (3f * 255f);
                sum += value;
                total++;
                if (value > 0.5f)
                {
                    bright++;
                }
            }

            if (total == 0)
            {
                note = "empty image";
                return false;
            }

            average = (float)(sum / total);
            brightShare = (float)bright / total;
            note = width + "x" + height + ", average " + (100f * average).ToString("0.#", CultureInfo.InvariantCulture)
                + "%, " + (100f * brightShare).ToString("0.#", CultureInfo.InvariantCulture) + "% of the pixels are bright";
            return true;
        }

        // Decodes a PNG or JPG into raw pixels (through a throwaway texture).
        private static Color32[] ReadPixels(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            byte[] bytes = File.ReadAllBytes(path);
            Texture2D temp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(temp, bytes, false))
                {
                    return null;
                }

                width = temp.width;
                height = temp.height;
                return temp.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.Destroy(temp);
            }
        }

        private static Texture2D MakeLinear(string name, int width, int height, Color32[] pixels)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, true, true);
            texture.name = name;
            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 8;
            texture.Apply(true, true); // maps are never read back: free the copy of the pixels kept in memory
            return texture;
        }

        // A normal map is bluish (about 128, 128, 255); an image whose channels are practically equal is a height map.
        private static bool IsGrayscale(Color32[] pixels)
        {
            int step = Math.Max(1, pixels.Length / 4000);
            int gray = 0;
            int total = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                Color32 c = pixels[i];
                total++;
                if (Math.Abs(c.r - c.g) <= 3 && Math.Abs(c.g - c.b) <= 3)
                {
                    gray++;
                }
            }

            return total > 0 && gray >= total * 0.98;
        }

        // Height to tangent-space normal (OpenGL convention: green up; the rows of the pixel array run bottom to top).
        // Central differences, wrapping at the edges because the texture repeats.
        private static Color32[] NormalFromHeight(Color32[] pixels, int width, int height, float strength)
        {
            float[] heights = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                heights[i] = (pixels[i].r + pixels[i].g + pixels[i].b) / (3f * 255f);
            }

            Color32[] result = new Color32[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int below = ((y + height - 1) % height) * width;
                int above = ((y + 1) % height) * width;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int left = (x + width - 1) % width;
                    int right = (x + 1) % width;
                    float dx = (heights[row + right] - heights[row + left]) * strength;
                    float dy = (heights[above + x] - heights[below + x]) * strength;
                    float inverse = 1f / Mathf.Sqrt(dx * dx + dy * dy + 1f);
                    result[row + x] = new Color32(
                        (byte)Mathf.RoundToInt((-dx * inverse * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((-dy * inverse * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((inverse * 0.5f + 0.5f) * 255f),
                        255);
                }
            }

            return result;
        }
    }
}
