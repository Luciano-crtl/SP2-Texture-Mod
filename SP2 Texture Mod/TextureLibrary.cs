using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// The user's texture folder: &lt;persistentDataPath&gt;/TextureMod (next to the game's own PaintTextures
    /// and CraftDecals folders). It holds PNG and JPG images. An image with an OBJ of the same name
    /// (Ta152.png + Ta152.obj) is applied by transferring the model's UVs; an image alone is tiled.
    /// </summary>
    internal static class TextureLibrary
    {
        internal sealed class Entry
        {
            public string FileName;
            public string FilePath;
            public string ModelFilePath; // matching .obj, or null
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static int _index;

        public static string Folder
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "TextureMod"); }
        }

        public static int Count
        {
            get { return Entries.Count; }
        }

        public static int Index
        {
            get { return _index; }
        }

        public static Entry Current
        {
            get { return Entries.Count > 0 ? Entries[_index] : null; }
        }

        /// <summary>Scans the folder (creating it, with a short README, the first time).</summary>
        public static void Refresh()
        {
            Entries.Clear();
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string readme = System.IO.Path.Combine(folder, "README.txt");
                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme,
                        "SP2 Texture Mod\r\n"
                        + "\r\n"
                        + "Put PNG or JPG images in this folder.\r\n"
                        + "- An image alone is tiled over the procedural parts (fuselage, wings...).\r\n"
                        + "- An image plus an OBJ model with the same name (Ta152.png + Ta152.obj) is mapped like the model:\r\n"
                        + "  the mod aligns the OBJ to your aircraft and copies its texture coordinates.\r\n"
                        + "  The OBJ must have UVs (vt lines).\r\n"
                        + "In the designer: Insert = apply/remove, PageUp/PageDown = choose image, Home/End = tiling.\r\n");
                }

                string[] files = Directory.GetFiles(folder);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                    {
                        continue;
                    }

                    Entry entry = new Entry();
                    entry.FileName = System.IO.Path.GetFileName(file);
                    entry.FilePath = file;
                    string obj = System.IO.Path.ChangeExtension(file, ".obj");
                    entry.ModelFilePath = File.Exists(obj) ? obj : null;
                    Entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not scan the texture folder: " + ex.Message);
            }

            _index = 0;
            string wanted = Plugin.SelectedTexture.Value;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (string.Equals(Entries[i].FileName, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    _index = i;
                    break;
                }
            }
        }

        /// <summary>Moves the selection (wrapping around) and remembers it in the settings.</summary>
        public static void Step(int delta)
        {
            if (Entries.Count == 0)
            {
                return;
            }

            _index = ((_index + delta) % Entries.Count + Entries.Count) % Entries.Count;
            Plugin.SelectedTexture.Value = Entries[_index].FileName;
        }

        /// <summary>Reads the image into a new texture (mipmapped, sRGB). Returns null if it cannot be read.</summary>
        public static Texture2D LoadTexture(Entry entry)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(entry.FilePath);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!ImageConversion.LoadImage(texture, bytes, false))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = entry.FileName;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 8;
                texture.Apply(true, false);
                return texture;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not read '" + entry.FileName + "': " + ex.Message);
                return null;
            }
        }
    }
}
