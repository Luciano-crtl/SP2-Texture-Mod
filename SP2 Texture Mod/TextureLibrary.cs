using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
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

        /// <summary>
        /// One of the extra maps of the material (normal, occlusion, height). It is chosen among the images of the
        /// folder, or "none"; when the albedo image changes it is found again by name (Ta152_a + Ta152_n).
        /// </summary>
        internal sealed class MapSlot
        {
            public readonly string Label;
            public readonly string[] Suffixes;
            public readonly Func<ConfigEntry<string>> Setting;
            public int Index = -1; // -1 = none

            public MapSlot(string label, string[] suffixes, Func<ConfigEntry<string>> setting)
            {
                Label = label;
                Suffixes = suffixes;
                Setting = setting;
            }

            public Entry Current
            {
                get { return Index >= 0 && Index < Images.Count ? Images[Index] : null; }
            }

            public string CurrentName
            {
                get { return Current != null ? Current.FileName : null; }
            }
        }

        public static readonly MapSlot Normal = new MapSlot("Normal map",
            new[] { "_n", "_nrm", "_nm", "_normal", "_normals", "_norm" }, () => Plugin.SelectedNormal);
        public static readonly MapSlot Occlusion = new MapSlot("Occlusion map",
            new[] { "_ao", "_occ", "_occlusion", "_ambientocclusion" }, () => Plugin.SelectedOcclusion);
        public static readonly MapSlot Height = new MapSlot("Height (bump) map",
            new[] { "_h", "_height", "_disp", "_displacement", "_bump", "_parallax" }, () => Plugin.SelectedHeight);

        private static readonly MapSlot[] Slots = { Normal, Occlusion, Height };

        // Endings that mark the albedo image of a set; removed to find the other maps of the set by name.
        private static readonly string[] AlbedoSuffixes =
            { "_a", "_d", "_diff", "_diffuse", "_albedo", "_basecolor", "_base_color", "_color", "_col" };

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
                        + "  An image and an OBJ with the same file name are paired automatically.\r\n"
                        + "- Other maps of the same set are found by name: for Ta152_a.jpg the mod looks for Ta152_n (normal),\r\n"
                        + "  Ta152_ao (occlusion) and Ta152_h or Ta152_bump (height). They can also be chosen in the panel.\r\n");
                }

                string[] files = Directory.GetFiles(folder);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (System.IO.Path.GetFileName(file).StartsWith("debug_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // files written by the mod itself (debug_uv_map.png)
                    }

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

            for (int s = 0; s < Slots.Length; s++)
            {
                RestoreSlot(Slots[s]);
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
            for (int s = 0; s < Slots.Length; s++)
            {
                AutoPairMap(Slots[s], true);
            }
        }

        /// <summary>Selects the next or previous image for a map slot, including "none".</summary>
        public static void StepMap(MapSlot slot, int delta)
        {
            int positions = Images.Count + 1;
            int position = ((slot.Index + 1 + delta) % positions + positions) % positions;
            slot.Index = position - 1;
            slot.Setting().Value = slot.Index >= 0 ? Images[slot.Index].FileName : "none";
        }

        // "auto" = find it by name next to the albedo image; "none" = no map; otherwise a file name.
        private static void RestoreSlot(MapSlot slot)
        {
            slot.Index = -1;
            string wanted = slot.Setting().Value;
            if (string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase))
            {
                slot.Index = FindMap(slot);
                return;
            }

            for (int i = 0; i < Images.Count; i++)
            {
                if (string.Equals(Images[i].FileName, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    slot.Index = i;
                    return;
                }
            }
        }

        private static void AutoPairMap(MapSlot slot, bool save)
        {
            slot.Index = FindMap(slot);
            if (save)
            {
                slot.Setting().Value = slot.Index >= 0 ? Images[slot.Index].FileName : "none";
            }
        }

        // Index of the image whose name is the albedo's name (without its ending) plus one of the slot's endings.
        private static int FindMap(MapSlot slot)
        {
            Entry albedo = CurrentImage;
            if (albedo == null)
            {
                return -1;
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(albedo.FileName);
            for (int a = 0; a < AlbedoSuffixes.Length; a++)
            {
                if (stem.EndsWith(AlbedoSuffixes[a], StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem.Substring(0, stem.Length - AlbedoSuffixes[a].Length);
                    break;
                }
            }

            for (int s = 0; s < slot.Suffixes.Length; s++)
            {
                string wanted = stem + slot.Suffixes[s];
                for (int i = 0; i < Images.Count; i++)
                {
                    if (i != _imageIndex
                        && string.Equals(System.IO.Path.GetFileNameWithoutExtension(Images[i].FileName), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
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
