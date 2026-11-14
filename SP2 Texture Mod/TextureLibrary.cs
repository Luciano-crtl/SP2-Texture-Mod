using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// The user's texture folder: &lt;persistentDataPath&gt;/TextureMod (next to the game's own PaintTextures
    /// and CraftDecals folders). It holds PNG/JPG images and OBJ models. The image and the model are chosen
    /// separately; an image with an OBJ of the same name (Ta152.png + Ta152.obj) is paired automatically.
    /// With a model the texture is mapped like the model, without one it is tiled.
    /// </summary>
    internal static class TextureLibrary
    {
        internal sealed class Entry
        {
            public string FileName;
            public string FilePath;
        }

        private static readonly List<Entry> Images = new List<Entry>();
        private static readonly List<Entry> Models = new List<Entry>();
        private static int _imageIndex;
        private static int _modelIndex = -1; // -1 = no model (tiled)

        public static string Folder
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "TextureMod"); }
        }

        public static int ImageCount
        {
            get { return Images.Count; }
        }

        public static int ImageIndex
        {
            get { return _imageIndex; }
        }

        public static int ModelCount
        {
            get { return Models.Count; }
        }

        public static int ModelIndex
        {
            get { return _modelIndex; }
        }

        public static Entry CurrentImage
        {
            get { return Images.Count > 0 ? Images[_imageIndex] : null; }
        }

        public static string CurrentModelName
        {
            get { return _modelIndex >= 0 && _modelIndex < Models.Count ? Models[_modelIndex].FileName : null; }
        }

        public static string CurrentModelPath
        {
            get { return _modelIndex >= 0 && _modelIndex < Models.Count ? Models[_modelIndex].FilePath : null; }
        }

        /// <summary>Scans the folder (creating it, with a short README, the first time) and restores the saved choice.</summary>
        public static void Refresh()
        {
            Images.Clear();
            Models.Clear();
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
                        + "Put PNG or JPG images and OBJ models in this folder.\r\n"
                        + "In the designer open the menu (the three-line button) and choose Textures.\r\n"
                        + "- An image without a model is tiled over the procedural parts (fuselage, wings...).\r\n"
                        + "- An image with an OBJ model of the same aircraft is mapped like the model: the mod aligns\r\n"
                        + "  the OBJ to your craft and copies its texture coordinates. The OBJ must have UVs (vt lines).\r\n"
                        + "  An image and an OBJ with the same file name are paired automatically.\r\n");
                }

                string[] files = Directory.GetFiles(folder);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    Entry entry = new Entry();
                    entry.FileName = System.IO.Path.GetFileName(file);
                    entry.FilePath = file;
                    if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
                    {
                        Images.Add(entry);
                    }
                    else if (extension == ".obj")
                    {
                        Models.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not scan the texture folder: " + ex.Message);
            }

            _imageIndex = 0;
            string wantedImage = Plugin.SelectedTexture.Value;
            for (int i = 0; i < Images.Count; i++)
            {
                if (string.Equals(Images[i].FileName, wantedImage, StringComparison.OrdinalIgnoreCase))
                {
                    _imageIndex = i;
                    break;
                }
            }

            // "auto" = pair with the same-named model if there is one; "none" = tiled; otherwise a file name.
            _modelIndex = -1;
            string wantedModel = Plugin.SelectedModel.Value;
            if (string.Equals(wantedModel, "auto", StringComparison.OrdinalIgnoreCase))
            {
                AutoPair(false);
            }
            else
            {
                for (int i = 0; i < Models.Count; i++)
                {
                    if (string.Equals(Models[i].FileName, wantedModel, StringComparison.OrdinalIgnoreCase))
                    {
                        _modelIndex = i;
                        break;
                    }
                }
            }
        }

        /// <summary>Selects the next or previous image (wrapping around); pairs it with a same-named model if any.</summary>
        public static void StepImage(int delta)
        {
            if (Images.Count == 0)
            {
                return;
            }

            _imageIndex = ((_imageIndex + delta) % Images.Count + Images.Count) % Images.Count;
            Plugin.SelectedTexture.Value = Images[_imageIndex].FileName;
            AutoPair(true);
        }

        /// <summary>Selects the next or previous model, including "none" (tiled).</summary>
        public static void StepModel(int delta)
        {
            int positions = Models.Count + 1;
            int position = ((_modelIndex + 1 + delta) % positions + positions) % positions;
            _modelIndex = position - 1;
            Plugin.SelectedModel.Value = _modelIndex >= 0 ? Models[_modelIndex].FileName : "none";
        }

        // Selects the model with the same base name as the current image, if there is one.
        private static void AutoPair(bool save)
        {
            Entry image = CurrentImage;
            if (image == null)
            {
                return;
            }

            string baseName = System.IO.Path.GetFileNameWithoutExtension(image.FileName);
            for (int i = 0; i < Models.Count; i++)
            {
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Models[i].FileName), baseName, StringComparison.OrdinalIgnoreCase))
                {
                    _modelIndex = i;
                    if (save)
                    {
                        Plugin.SelectedModel.Value = Models[i].FileName;
                    }

                    return;
                }
            }
        }

        /// <summary>Opens the folder in the system's file browser.</summary>
        public static void OpenFolder()
        {
            try
            {
                Application.OpenURL("file:///" + Folder.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not open the folder: " + ex.Message);
            }
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
