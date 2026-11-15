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
    /// The other images of a set (normal, occlusion, specular, height) are found by name next to the albedo.
    /// </summary>
    internal static class TextureLibrary
    {
        internal sealed class Entry
        {
            public string FileName;
            public string FilePath;
        }

        /// <summary>
        /// One of the extra maps of the material (normal, occlusion, specular, height). It is chosen among the
        /// images of the folder, or "none"; when the albedo image changes it is found again by name
        /// (Ta152_a + Ta152_n).
        /// </summary>
        internal sealed class MapSlot
        {
            public readonly string Label;
            public readonly string[] Suffixes;
            public readonly Func<ConfigEntry<string>> Setting;
            public int Index = -1; // -1 = none; otherwise an index into AllImages

            public MapSlot(string label, string[] suffixes, Func<ConfigEntry<string>> setting)
            {
                Label = label;
                Suffixes = suffixes;
                Setting = setting;
            }

            public Entry Current
            {
                get { return Index >= 0 && Index < AllImages.Count ? AllImages[Index] : null; }
            }

            public string CurrentName
            {
                get { return Current != null ? Current.FileName : null; }
            }
        }

        // The endings include the ones of the sets exported from War Thunder (fw_190d_9_a, fw_190d_9_n_n,
        // fw_190d_9_n_ao, fw_190d_9_n_s) as well as the usual ones.
        public static readonly MapSlot Normal = new MapSlot("Normal map",
            new[] { "_n_n", "_n", "_nrm", "_nm", "_normal", "_normals", "_norm" }, () => Plugin.SelectedNormal);
        public static readonly MapSlot Occlusion = new MapSlot("Occlusion map",
            new[] { "_n_ao", "_ao", "_occ", "_occlusion", "_ambientocclusion" }, () => Plugin.SelectedOcclusion);
        public static readonly MapSlot Specular = new MapSlot("Specular map",
            new[] { "_n_s", "_s", "_spec", "_specular", "_gloss", "_smoothness", "_metallic" }, () => Plugin.SelectedSpecular);
        public static readonly MapSlot Height = new MapSlot("Height (bump) map",
            new[] { "_h", "_height", "_disp", "_displacement", "_bump", "_parallax" }, () => Plugin.SelectedHeight);

        private static readonly MapSlot[] Slots = { Normal, Occlusion, Specular, Height };

        // Endings that mark the albedo image of a set; removed to find the other maps of the set by name.
        private static readonly string[] AlbedoSuffixes =
            { "_a", "_d", "_diff", "_diffuse", "_albedo", "_basecolor", "_base_color", "_color", "_col" };

        private const string ReadmeMarker = "Readme version 3";

        // Every PNG/JPG of the folder: what the map slots choose among.
        private static readonly List<Entry> AllImages = new List<Entry>();

        // The images that can be the albedo of a set: AllImages without the map files that belong to an albedo
        // (so "Next image" does not stop at fw_190d_9_n_n.jpg).
        private static readonly List<Entry> Albedos = new List<Entry>();

        private static readonly List<Entry> Models = new List<Entry>();
        private static int _imageIndex;       // into Albedos
        private static int _modelIndex = -1;  // -1 = no model (tiled)

        public static string Folder
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "TextureMod"); }
        }

        public static int ImageCount
        {
            get { return Albedos.Count; }
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
            get { return Albedos.Count > 0 ? Albedos[Mathf.Clamp(_imageIndex, 0, Albedos.Count - 1)] : null; }
        }

        public static string CurrentModelName
        {
            get { return _modelIndex >= 0 && _modelIndex < Models.Count ? Models[_modelIndex].FileName : null; }
        }

        public static string CurrentModelPath
        {
            get { return _modelIndex >= 0 && _modelIndex < Models.Count ? Models[_modelIndex].FilePath : null; }
        }

        /// <summary>Full path of a file of the folder, or null if it is not there.</summary>
        public static string PathFor(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string path = System.IO.Path.Combine(Folder, fileName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Scans the folder (creating it, with a short README, the first time) and restores the saved choice.</summary>
        public static void Refresh()
        {
            AllImages.Clear();
            Albedos.Clear();
            Models.Clear();
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                WriteReadme(folder);

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
                        AllImages.Add(entry);
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

            foreach (Entry entry in AllImages)
            {
                if (!IsMapFileOfASet(entry))
                {
                    Albedos.Add(entry);
                }
            }

            if (Albedos.Count == 0)
            {
                Albedos.AddRange(AllImages); // never leave the user with nothing to choose
            }

            _imageIndex = 0;
            string wantedImage = Plugin.SelectedTexture.Value;
            for (int i = 0; i < Albedos.Count; i++)
            {
                if (string.Equals(Albedos[i].FileName, wantedImage, StringComparison.OrdinalIgnoreCase))
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
                _modelIndex = IndexOfName(Models, wantedModel);
            }
        }

        private static void WriteReadme(string folder)
        {
            string readme = System.IO.Path.Combine(folder, "README.txt");
            try
            {
                if (File.Exists(readme) && File.ReadAllText(readme).Contains(ReadmeMarker))
                {
                    return;
                }

                File.WriteAllText(readme,
                    "SP2 Texture Mod (" + ReadmeMarker + ")\r\n"
                    + "\r\n"
                    + "Put PNG or JPG images and OBJ models in this folder.\r\n"
                    + "In the designer open the menu (the three-line button) and choose Textures.\r\n"
                    + "- An image without a model is tiled over the procedural parts (fuselage, wings...).\r\n"
                    + "- An image with an OBJ model of the same aircraft is mapped like the model: the mod aligns\r\n"
                    + "  the OBJ to your craft and copies its texture coordinates. The OBJ must have UVs (vt lines).\r\n"
                    + "  An image and an OBJ with the same file name are paired automatically.\r\n"
                    + "- The other images of the same set are found by name next to the albedo image.\r\n"
                    + "  For fw_190d_9_a.jpg the mod looks for fw_190d_9_n_n (normal), fw_190d_9_n_ao (occlusion) and\r\n"
                    + "  fw_190d_9_n_s (specular); the usual endings work too: _n, _nrm, _normal, _ao, _occlusion,\r\n"
                    + "  _s, _spec, _specular, _h, _height, _bump. They can also be chosen in the panel.\r\n"
                    + "  The SimplePlanes 2 part shader reads normal, occlusion and height maps. It has no specular map:\r\n"
                    + "  only the average of the specular image is used, as a general glossiness of the whole craft.\r\n"
                    + "- When you apply a texture in the designer it is saved for the craft (by its name) in the Crafts\r\n"
                    + "  folder next to this file. Opening that craft in the designer or flying it puts the texture on\r\n"
                    + "  again. Remove in the panel takes the texture off and forgets it. The craft file is never changed.\r\n"
                    + "- The texture coordinates are saved with the craft too (a .uv file next to its .txt), tied to the meshes and not to\r\n"
                    + "  where the parts are. So an aircraft with moving parts (a swing wing, folding gear) must be in the SAME POSE AS THE\r\n"
                    + "  OBJ MODEL when you press Apply (an F-14 model has its wings forward: put the wings forward, apply, then sweep them\r\n"
                    + "  back and save the craft). The panel's \"Model fit\" row says POOR when the model does not match the craft.\r\n");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not write the README: " + ex.Message);
            }
        }

        /// <summary>Selects the next or previous image (wrapping around); pairs it with a same-named model if any.</summary>
        public static void StepImage(int delta)
        {
            if (Albedos.Count == 0)
            {
                return;
            }

            _imageIndex = ((_imageIndex + delta) % Albedos.Count + Albedos.Count) % Albedos.Count;
            Plugin.SelectedTexture.Value = Albedos[_imageIndex].FileName;
            AutoPair(true);
            for (int s = 0; s < Slots.Length; s++)
            {
                AutoPairMap(Slots[s], true);
            }
        }

        /// <summary>Selects the next or previous image for a map slot, including "none".</summary>
        public static void StepMap(MapSlot slot, int delta)
        {
            int positions = AllImages.Count + 1;
            int position = ((slot.Index + 1 + delta) % positions + positions) % positions;
            slot.Index = position - 1;
            slot.Setting().Value = slot.Index >= 0 ? AllImages[slot.Index].FileName : "none";
        }

        /// <summary>
        /// Selects what a saved craft assignment names: image, model and maps (maps and model that are no longer in
        /// the folder are dropped). Returns false, with the reason, if the image itself is missing.
        /// The settings are changed in memory only; the caller saves the configuration once.
        /// </summary>
        public static bool Select(CraftAssignment assignment, out string problem)
        {
            problem = null;
            Refresh();

            int image = IndexOfName(Albedos, assignment.Image);
            if (image < 0)
            {
                problem = "the image '" + assignment.Image + "' is not in the texture folder";
                return false;
            }

            _imageIndex = image;
            Plugin.Set(Plugin.SelectedTexture, Albedos[image].FileName);

            if (string.IsNullOrEmpty(assignment.Model))
            {
                _modelIndex = -1;
                Plugin.Set(Plugin.SelectedModel, "none");
            }
            else
            {
                int model = IndexOfName(Models, assignment.Model);
                if (model < 0)
                {
                    problem = "the model '" + assignment.Model + "' is not in the texture folder";
                    return false;
                }

                _modelIndex = model;
                Plugin.Set(Plugin.SelectedModel, Models[model].FileName);
            }

            SelectSlot(Normal, assignment.Normal);
            SelectSlot(Occlusion, assignment.Occlusion);
            SelectSlot(Specular, assignment.Specular);
            SelectSlot(Height, assignment.Height);
            return true;
        }

        private static void SelectSlot(MapSlot slot, string fileName)
        {
            int index = string.IsNullOrEmpty(fileName) ? -1 : IndexOfName(AllImages, fileName);
            slot.Index = index;
            Plugin.Set(slot.Setting(), index >= 0 ? AllImages[index].FileName : "none");
        }

        private static int IndexOfName(List<Entry> list, string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
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

            slot.Index = IndexOfName(AllImages, wanted);
        }

        // The slot follows the albedo image by name. What is saved is "auto", never "none": "none" is only for a map
        // the user switched off on purpose, otherwise stepping through the images would switch maps off for good.
        private static void AutoPairMap(MapSlot slot, bool save)
        {
            slot.Index = FindMap(slot);
            if (save)
            {
                slot.Setting().Value = "auto";
            }
        }

        // The albedo's file name without its extension and without its albedo ending: the name of the set.
        private static string SetName(Entry albedo)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(albedo.FileName);
            for (int a = 0; a < AlbedoSuffixes.Length; a++)
            {
                if (stem.EndsWith(AlbedoSuffixes[a], StringComparison.OrdinalIgnoreCase))
                {
                    return stem.Substring(0, stem.Length - AlbedoSuffixes[a].Length);
                }
            }

            return stem;
        }

        // Index (in AllImages) of the image whose name is the albedo's set name plus one of the slot's endings.
        private static int FindMap(MapSlot slot)
        {
            Entry albedo = CurrentImage;
            if (albedo == null)
            {
                return -1;
            }

            string stem = SetName(albedo);
            for (int s = 0; s < slot.Suffixes.Length; s++)
            {
                string wanted = stem + slot.Suffixes[s];
                for (int i = 0; i < AllImages.Count; i++)
                {
                    if (AllImages[i] != albedo
                        && string.Equals(System.IO.Path.GetFileNameWithoutExtension(AllImages[i].FileName), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        // A map file of a set: its name is some albedo image's set name plus a map ending (fw_190d_9_n_ao next to
        // fw_190d_9_a). Images that merely end like a map but have no albedo next to them stay in the list.
        private static bool IsMapFileOfASet(Entry image)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(image.FileName);
            for (int s = 0; s < Slots.Length; s++)
            {
                for (int x = 0; x < Slots[s].Suffixes.Length; x++)
                {
                    string suffix = Slots[s].Suffixes[x];
                    if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && HasAlbedoOfSet(stem.Substring(0, stem.Length - suffix.Length), image))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasAlbedoOfSet(string setName, Entry except)
        {
            for (int i = 0; i < AllImages.Count; i++)
            {
                if (AllImages[i] == except)
                {
                    continue;
                }

                string stem = System.IO.Path.GetFileNameWithoutExtension(AllImages[i].FileName);
                if (string.Equals(stem, setName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                for (int a = 0; a < AlbedoSuffixes.Length; a++)
                {
                    if (string.Equals(stem, setName + AlbedoSuffixes[a], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
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
            return LoadTexture(entry.FilePath, entry.FileName);
        }

        /// <summary>
        /// Reads an image file into a new texture (mipmapped, sRGB). Returns null if it cannot be read.
        /// keepReadable = false drops the copy of the pixels kept in memory once the texture is on the graphics card
        /// (half the memory; only the designer's debug map needs to read the albedo back).
        /// </summary>
        public static Texture2D LoadTexture(string filePath, string name, bool keepReadable = true)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!ImageConversion.LoadImage(texture, bytes, false))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = name;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 8;
                texture.Apply(true, !keepReadable);
                return texture;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not read '" + name + "': " + ex.Message);
                return null;
            }
        }
    }
}
