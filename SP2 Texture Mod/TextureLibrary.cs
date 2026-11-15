using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// The user's texture folder: &lt;persistentDataPath&gt;/TextureMod (next to the game's own PaintTextures and CraftDecals
    /// folders). It holds SETS, one per aircraft, each a sub-folder (TextureMod/fw 190, TextureMod/b2) with the 3D model
    /// (.glb, .gltf or .obj) and the images (PNG/JPG). The files left directly in TextureMod form one more set, "(main
    /// folder)". The user picks a set; the model, the albedo image and the other maps (normal, occlusion, metalness,
    /// roughness, specular, height) are found by themselves: from the material of a glTF/GLB model, or by file names for an
    /// OBJ. Anything can still be changed by hand. With a model the texture is mapped like the model, without one it is tiled.
    /// Files are named everywhere (settings, saved crafts) by their path relative to the TextureMod folder, with '/'.
    /// </summary>
    internal static class TextureLibrary
    {
        internal sealed class Entry
        {
            public string FileName;   // the name alone
            public string FilePath;   // the full path
            public string Relative;   // the path from the TextureMod folder, with '/'
        }

        internal sealed class SetInfo
        {
            public string Name;       // what the panel shows
            public string Key;        // saved in the settings: the folder name, or "." for the main folder
            public string Path;
        }

        /// <summary>
        /// One of the extra maps of the material (normal, occlusion, specular, metalness, roughness, height). It is chosen among
        /// the images of the set, or "none"; when the albedo image changes it is found again by name or from the glTF material.
        /// </summary>
        internal sealed class MapSlot
        {
            public readonly string Label;
            public readonly string[] Suffixes;
            public readonly Func<ConfigEntry<string>> Setting;
            public readonly Func<string, bool> NameFits; // on a lower-case file name without extension: does it look like this map?
            public readonly Func<GltfDocument.ExtractedFiles, string> FromGltf; // the file the glTF material names for this slot
            public int Index = -1; // -1 = none; otherwise an index into AllImages

            public MapSlot(string label, string[] suffixes, Func<ConfigEntry<string>> setting, Func<string, bool> nameFits,
                Func<GltfDocument.ExtractedFiles, string> fromGltf)
            {
                Label = label;
                Suffixes = suffixes;
                Setting = setting;
                NameFits = nameFits;
                FromGltf = fromGltf;
            }

            public Entry Current
            {
                get { return Index >= 0 && Index < AllImages.Count ? AllImages[Index] : null; }
            }

            public string CurrentName
            {
                get { return Current != null ? Current.FileName : null; }
            }

            public string CurrentRelative
            {
                get { return Current != null ? Current.Relative : null; }
            }
        }

        // The endings include the ones of the sets exported from War Thunder (fw_190d_9_a, fw_190d_9_n_n,
        // fw_190d_9_n_ao, fw_190d_9_n_s) as well as the usual ones.
        public static readonly MapSlot Normal = new MapSlot("Normal map",
            new[] { "_n_n", "_n", "_nrm", "_nm", "_normal", "_normals", "_norm" }, () => Plugin.SelectedNormal,
            n => n.Contains("normal"), g => g.Normal);
        public static readonly MapSlot Occlusion = new MapSlot("Occlusion map",
            new[] { "_n_ao", "_ao", "_occ", "_occlusion", "_ambientocclusion" }, () => Plugin.SelectedOcclusion,
            n => n.Contains("occlusion") || n.EndsWith("_ao", StringComparison.Ordinal), g => g.Occlusion);
        public static readonly MapSlot Specular = new MapSlot("Specular / gloss map",
            new[] { "_n_s", "_s", "_spec", "_specular", "_gloss", "_smoothness" }, () => Plugin.SelectedSpecular,
            n => n.Contains("spec") || n.Contains("gloss") || n.Contains("smooth"), g => null);
        // A converter that splits a glTF metallic-roughness image writes "...@channels=B" (metallic) and "...@channels=G"
        // (roughness); a single image named with both words has metallic in blue and roughness in green.
        public static readonly MapSlot Metalness = new MapSlot("Metalness map",
            new[] { "_metallic", "_metalness", "_metal", "_met" }, () => Plugin.SelectedMetalness,
            n => n.Contains("metal") && ChannelTagIs(n, 'b'), g => g.MetalRough);
        public static readonly MapSlot Roughness = new MapSlot("Roughness map",
            new[] { "_roughness", "_rough" }, () => Plugin.SelectedRoughness,
            n => n.Contains("rough") && ChannelTagIs(n, 'g'), g => g.MetalRough);
        public static readonly MapSlot Height = new MapSlot("Height (bump) map",
            new[] { "_h", "_height", "_disp", "_displacement", "_bump", "_parallax" }, () => Plugin.SelectedHeight,
            n => n.Contains("height") || n.Contains("bump") || n.Contains("displace"), g => null);

        private static readonly MapSlot[] Slots = { Normal, Occlusion, Specular, Metalness, Roughness, Height };

        // "...@channels=B": true if there is no tag, or the tag names this channel.
        private static bool ChannelTagIs(string lowerName, char channel)
        {
            int tag = lowerName.IndexOf("channels=", StringComparison.Ordinal);
            return tag < 0 || tag + 9 >= lowerName.Length || lowerName[tag + 9] == channel;
        }

        // Endings that mark the albedo image of a set; removed to find the other maps of the set by name.
        private static readonly string[] AlbedoSuffixes =
            { "_a", "_d", "_diff", "_diffuse", "_albedo", "_basecolor", "_base_color", "_color", "_col", "basecolor", "albedo", "diffuse" };

        private const string ReadmeMarker = "Readme version 4";
        private const string MainKey = ".";
        private const string MainName = "(files in the main folder)";

        private static readonly List<SetInfo> Sets = new List<SetInfo>();
        private static int _setIndex;

        // Every PNG/JPG of the current set (and the images taken out of its glTF): what the map slots choose among.
        private static readonly List<Entry> AllImages = new List<Entry>();

        // The images that can be the albedo of a set: AllImages without the map files that belong to an albedo
        // (so "Next image" does not stop at fw_190d_9_n_n.jpg).
        private static readonly List<Entry> Albedos = new List<Entry>();

        private static readonly List<Entry> Models = new List<Entry>();
        private static int _imageIndex;       // into Albedos
        private static int _modelIndex = -1;  // -1 = no model (tiled)

        // The images of the current glTF/GLB model's main material (written into TextureMod/_cache); null for an OBJ.
        private static GltfDocument.ExtractedFiles _gltfFiles;
        private static string _gltfKey;
        private static GltfDocument.ExtractedFiles _gltfKept;

        /// <summary>Why the model's images could not be taken (shown by the panel), or empty.</summary>
        public static string ModelProblem = "";

        public static string Folder
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "TextureMod"); }
        }

        // ---------------------------------------------------------------- what the panel and the applier read

        public static int SetCount
        {
            get { return Sets.Count; }
        }

        public static int SetIndex
        {
            get { return _setIndex; }
        }

        public static string CurrentSetName
        {
            get { return Sets.Count > 0 ? Sets[Mathf.Clamp(_setIndex, 0, Sets.Count - 1)].Name : null; }
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

        public static string CurrentModelRelative
        {
            get { return _modelIndex >= 0 && _modelIndex < Models.Count ? Models[_modelIndex].Relative : null; }
        }

        /// <summary>Full path of a file of the folder given by its relative path (or a bare name in the main folder), or null if it is not there.</summary>
        public static string PathFor(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return null;
            }

            string path = System.IO.Path.Combine(Folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        /// <summary>The path of a file below the texture folder, relative to it, with '/'.</summary>
        public static string RelativeOf(string fullPath)
        {
            string root = System.IO.Path.GetFullPath(Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, '/', '\\');
            string full = System.IO.Path.GetFullPath(fullPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                full = full.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/', '\\');
            }

            return full.Replace('\\', '/');
        }

        // ---------------------------------------------------------------- scanning

        /// <summary>Scans the folder (creating it, with a short README, the first time) and restores the saved choice.</summary>
        public static void Refresh()
        {
            ScanSets();

            // The saved set; else the main folder if the saved image is one of its files (settings of an older version); else the first.
            _setIndex = 0;
            string wanted = Plugin.SelectedSet.Value;
            int found = -1;
            for (int i = 0; i < Sets.Count; i++)
            {
                if (string.Equals(Sets[i].Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0 && !string.IsNullOrEmpty(Plugin.SelectedTexture.Value) && !Plugin.SelectedTexture.Value.Contains("/"))
            {
                for (int i = 0; i < Sets.Count; i++)
                {
                    if (Sets[i].Key == MainKey && File.Exists(System.IO.Path.Combine(Sets[i].Path, Plugin.SelectedTexture.Value)))
                    {
                        found = i;
                        break;
                    }
                }
            }

            _setIndex = Math.Max(0, found);
            LoadCurrentSet(true);
        }

        private static void ScanSets()
        {
            Sets.Clear();
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                WriteReadme(folder);

                string[] directories = Directory.GetDirectories(folder);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    string name = System.IO.Path.GetFileName(directory);
                    if (name.StartsWith("_", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal)
                        || string.Equals(name, "Crafts", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (HasUsefulFiles(directory, 0, 2))
                    {
                        SetInfo info = new SetInfo();
                        info.Name = name;
                        info.Key = name;
                        info.Path = directory;
                        Sets.Add(info);
                    }
                }

                if (HasUsefulFiles(folder, 0, 0))
                {
                    SetInfo main = new SetInfo();
                    main.Name = MainName;
                    main.Key = MainKey;
                    main.Path = folder;
                    Sets.Add(main);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not scan the texture folder: " + ex.Message);
            }
        }

        private static bool HasUsefulFiles(string directory, int depth, int maxDepth)
        {
            foreach (string file in Directory.GetFiles(directory))
            {
                if (KindOf(file) != 0 && !System.IO.Path.GetFileName(file).StartsWith("debug_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (depth < maxDepth)
            {
                foreach (string sub in Directory.GetDirectories(directory))
                {
                    if (HasUsefulFiles(sub, depth + 1, maxDepth))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // 1 = image, 2 = model, 0 = something else.
        private static int KindOf(string file)
        {
            string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
            {
                return 1;
            }

            return extension == ".obj" || extension == ".glb" || extension == ".gltf" ? 2 : 0;
        }

        // Reads the files of the current set and picks image, model and maps. restore: take the saved choices of the settings
        // (when the program starts or the list is refreshed); otherwise everything is found again.
        private static void LoadCurrentSet(bool restore)
        {
            AllImages.Clear();
            Albedos.Clear();
            Models.Clear();
            _gltfFiles = null;
            ModelProblem = "";
            _imageIndex = 0;
            _modelIndex = -1;
            for (int s = 0; s < Slots.Length; s++)
            {
                Slots[s].Index = -1;
            }

            if (Sets.Count == 0)
            {
                return;
            }

            SetInfo set = Sets[Mathf.Clamp(_setIndex, 0, Sets.Count - 1)];
            try
            {
                CollectFiles(set.Path, set.Key == MainKey ? 0 : 2, 0);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not read the set '" + set.Name + "': " + ex.Message);
            }

            AllImages.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
            Models.Sort((a, b) => ModelRank(a).CompareTo(ModelRank(b)) != 0 ? ModelRank(a).CompareTo(ModelRank(b))
                : string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
            RebuildAlbedos();

            // The saved image, else the one that looks like an albedo.
            _imageIndex = restore ? IndexOfKey(Albedos, Plugin.SelectedTexture.Value) : -1;
            if (_imageIndex < 0)
            {
                _imageIndex = BestAlbedo();
            }

            // The model: the saved one, else the one that goes with the image, else the first (glb, gltf, then obj).
            string wantedModel = restore ? Plugin.SelectedModel.Value : "auto";
            if (string.Equals(wantedModel, "none", StringComparison.OrdinalIgnoreCase))
            {
                _modelIndex = -1;
            }
            else
            {
                _modelIndex = string.Equals(wantedModel, "auto", StringComparison.OrdinalIgnoreCase) ? -1 : IndexOfKey(Models, wantedModel);
                if (_modelIndex < 0 && !string.Equals(wantedModel, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _modelIndex = AutoModel();
                }
            }

            // A glTF model brings its own images (they join the set's images, so the albedo list may change).
            UpdateGltf();
            if (_gltfFiles != null)
            {
                int saved = restore ? IndexOfKey(Albedos, Plugin.SelectedTexture.Value) : -1;
                if (saved >= 0)
                {
                    _imageIndex = saved;
                }
                else
                {
                    SelectAlbedoFromGltf();
                }
            }

            for (int s = 0; s < Slots.Length; s++)
            {
                if (restore)
                {
                    RestoreSlot(Slots[s]);
                }
                else
                {
                    Slots[s].Index = FindMap(Slots[s]);
                }
            }
        }

        private static void CollectFiles(string directory, int maxDepth, int depth)
        {
            string[] files = Directory.GetFiles(directory);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                int kind = KindOf(file);
                if (kind == 0 || System.IO.Path.GetFileName(file).StartsWith("debug_", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // (debug_uv_map.png is written by the mod itself)
                }

                Entry entry = MakeEntry(file);
                if (kind == 1)
                {
                    AllImages.Add(entry);
                }
                else
                {
                    Models.Add(entry);
                }
            }

            if (depth < maxDepth)
            {
                foreach (string sub in Directory.GetDirectories(directory))
                {
                    string name = System.IO.Path.GetFileName(sub);
                    if (!name.StartsWith("_", StringComparison.Ordinal) && !name.StartsWith(".", StringComparison.Ordinal))
                    {
                        CollectFiles(sub, maxDepth, depth + 1);
                    }
                }
            }
        }

        private static Entry MakeEntry(string file)
        {
            Entry entry = new Entry();
            entry.FileName = System.IO.Path.GetFileName(file);
            entry.FilePath = file;
            entry.Relative = RelativeOf(file);
            return entry;
        }

        private static int ModelRank(Entry model)
        {
            string extension = System.IO.Path.GetExtension(model.FileName).ToLowerInvariant();
            return extension == ".glb" ? 0 : (extension == ".gltf" ? 1 : 2);
        }

        private static void RebuildAlbedos()
        {
            Albedos.Clear();
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
        }

        // The albedo candidate whose name says it (basecolor, albedo, diffuse, _a, _d), else the first.
        private static int BestAlbedo()
        {
            for (int i = 0; i < Albedos.Count; i++)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(Albedos[i].FileName).ToLowerInvariant();
                if (name.Contains("basecolor") || name.Contains("base_color") || name.Contains("albedo") || name.Contains("diffuse")
                    || name.EndsWith("_a", StringComparison.Ordinal) || name.EndsWith("_d", StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        // The model that goes with the current image (same base name), else the first one.
        private static int AutoModel()
        {
            if (Models.Count == 0)
            {
                return -1;
            }

            Entry image = CurrentImage;
            if (image != null)
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(image.FileName);
                for (int i = 0; i < Models.Count; i++)
                {
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Models[i].FileName), baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        // ---------------------------------------------------------------- glTF / GLB models

        // For a glTF/GLB model: reads its main material and writes its images into TextureMod/_cache/<set>/ (once per file).
        private static void UpdateGltf()
        {
            _gltfFiles = null;
            ModelProblem = "";
            string path = CurrentModelPath;
            if (path == null || !GltfDocument.IsGltf(path))
            {
                return;
            }

            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (key == _gltfKey && _gltfKept != null)
            {
                _gltfFiles = _gltfKept;
            }
            else
            {
                string error;
                GltfDocument document = GltfDocument.Open(path, out error);
                GltfDocument.Primary primary = document != null ? document.FindPrimary(out error) : null;
                if (primary == null)
                {
                    ModelProblem = error ?? "the model could not be read";
                    Plugin.Log.LogWarning("[TEX] " + System.IO.Path.GetFileName(path) + ": " + ModelProblem);
                    return;
                }

                SetInfo set = Sets[Mathf.Clamp(_setIndex, 0, Sets.Count - 1)];
                string directory = System.IO.Path.Combine(System.IO.Path.Combine(Folder, "_cache"), GltfDocument.SafeFileName(set.Key == MainKey ? "main" : set.Name));
                string prefix = GltfDocument.SafeFileName(System.IO.Path.GetFileNameWithoutExtension(path));
                GltfDocument.ExtractedFiles files = document.Extract(primary, directory, prefix, RelativeOf, out error);
                if (files == null)
                {
                    ModelProblem = error ?? "the images of the model could not be taken";
                    Plugin.Log.LogWarning("[TEX] " + System.IO.Path.GetFileName(path) + ": " + ModelProblem);
                    return;
                }

                Plugin.Log.LogInfo("[TEX] glTF '" + System.IO.Path.GetFileName(path) + "': material '" + primary.MaterialName + "' covers "
                    + primary.Triangles + " triangles; base color " + files.BaseColor + ", normal " + (files.Normal ?? "-")
                    + ", occlusion " + (files.Occlusion ?? "-") + ", metallic-roughness " + (files.MetalRough ?? "-"));
                _gltfKey = key;
                _gltfKept = files;
                _gltfFiles = files;
            }

            // Their entries join the images of the set.
            EnsureEntry(_gltfFiles.BaseColor);
            EnsureEntry(_gltfFiles.Normal);
            EnsureEntry(_gltfFiles.Occlusion);
            EnsureEntry(_gltfFiles.MetalRough);
            RebuildAlbedos();
        }

        // The entry of an image given by its relative path; added to the set's images if it is a file that exists.
        private static Entry EnsureEntry(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return null;
            }

            int index = IndexOfKey(AllImages, relative);
            if (index >= 0)
            {
                return AllImages[index];
            }

            string path = PathFor(relative);
            if (path == null)
            {
                return null;
            }

            Entry entry = MakeEntry(path);
            AllImages.Add(entry);
            return entry;
        }

        private static void SelectAlbedoFromGltf()
        {
            Entry baseColor = _gltfFiles != null ? EnsureEntry(_gltfFiles.BaseColor) : null;
            if (baseColor == null)
            {
                return;
            }

            RebuildAlbedos();
            int index = Albedos.IndexOf(baseColor);
            if (index < 0)
            {
                Albedos.Insert(0, baseColor);
                index = 0;
            }

            _imageIndex = index;
        }

        // ---------------------------------------------------------------- choosing

        /// <summary>Selects the next or previous set (folder) and finds its model, image and maps by themselves.</summary>
        public static void StepSet(int delta)
        {
            if (Sets.Count == 0)
            {
                return;
            }

            _setIndex = ((_setIndex + delta) % Sets.Count + Sets.Count) % Sets.Count;
            Plugin.SelectedSet.Value = Sets[_setIndex].Key;
            Plugin.SelectedTexture.Value = "";
            Plugin.SelectedModel.Value = "auto";
            for (int s = 0; s < Slots.Length; s++)
            {
                Slots[s].Setting().Value = "auto";
            }

            LoadCurrentSet(false);
            SaveCurrentChoice();
        }

        /// <summary>Selects the next or previous image (wrapping around); pairs it with a same-named model if any.</summary>
        public static void StepImage(int delta)
        {
            if (Albedos.Count == 0)
            {
                return;
            }

            _imageIndex = ((_imageIndex + delta) % Albedos.Count + Albedos.Count) % Albedos.Count;
            Plugin.SelectedTexture.Value = Albedos[_imageIndex].Relative;
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
            slot.Setting().Value = slot.Index >= 0 ? AllImages[slot.Index].Relative : "none";
        }

        /// <summary>Selects the next or previous model, including "none" (tiled); a glTF model brings its own images.</summary>
        public static void StepModel(int delta)
        {
            int positions = Models.Count + 1;
            int position = ((_modelIndex + 1 + delta) % positions + positions) % positions;
            _modelIndex = position - 1;
            Plugin.SelectedModel.Value = _modelIndex >= 0 ? Models[_modelIndex].Relative : "none";
            UpdateGltf();
            if (_gltfFiles != null)
            {
                SelectAlbedoFromGltf();
                Plugin.SelectedTexture.Value = CurrentImage != null ? CurrentImage.Relative : "";
            }

            for (int s = 0; s < Slots.Length; s++)
            {
                AutoPairMap(Slots[s], true);
            }
        }

        private static void SaveCurrentChoice()
        {
            Plugin.SelectedTexture.Value = CurrentImage != null ? CurrentImage.Relative : "";
            Plugin.SelectedModel.Value = _modelIndex >= 0 ? Models[_modelIndex].Relative : "auto";
        }

        /// <summary>
        /// Selects what a saved craft assignment names: the set (the folder of its model, or of its image), image, model
        /// and maps (maps that are no longer there are dropped). Returns false, with the reason, if the image or model is missing.
        /// The settings are changed in memory only; the caller saves the configuration once.
        /// </summary>
        public static bool Select(CraftAssignment assignment, out string problem)
        {
            problem = null;
            ScanSets();

            string anchor = !string.IsNullOrEmpty(assignment.Model) ? assignment.Model : assignment.Image;
            string setKey = MainKey;
            int slash = anchor != null ? anchor.IndexOf('/') : -1;
            if (slash > 0 && !anchor.StartsWith("_cache/", StringComparison.OrdinalIgnoreCase))
            {
                setKey = anchor.Substring(0, slash);
            }

            _setIndex = 0;
            for (int i = 0; i < Sets.Count; i++)
            {
                if (string.Equals(Sets[i].Key, setKey, StringComparison.OrdinalIgnoreCase))
                {
                    _setIndex = i;
                    break;
                }
            }

            Plugin.Set(Plugin.SelectedSet, Sets.Count > 0 ? Sets[_setIndex].Key : MainKey);
            LoadCurrentSet(false);

            Entry image = EnsureEntry(assignment.Image) ?? FindByName(AllImages, assignment.Image);
            if (image == null)
            {
                problem = "the image '" + assignment.Image + "' is not in the texture folder";
                return false;
            }

            RebuildAlbedos();
            int albedoIndex = Albedos.IndexOf(image);
            if (albedoIndex < 0)
            {
                Albedos.Insert(0, image);
                albedoIndex = 0;
            }

            _imageIndex = albedoIndex;
            Plugin.Set(Plugin.SelectedTexture, image.Relative);

            if (string.IsNullOrEmpty(assignment.Model))
            {
                _modelIndex = -1;
                _gltfFiles = null;
                Plugin.Set(Plugin.SelectedModel, "none");
            }
            else
            {
                int model = IndexOfKey(Models, assignment.Model);
                if (model < 0)
                {
                    problem = "the model '" + assignment.Model + "' is not in the texture folder";
                    return false;
                }

                _modelIndex = model;
                Plugin.Set(Plugin.SelectedModel, Models[model].Relative);
                UpdateGltf();
            }

            SelectSlot(Normal, assignment.Normal);
            SelectSlot(Occlusion, assignment.Occlusion);
            SelectSlot(Specular, assignment.Specular);
            SelectSlot(Metalness, assignment.MetallicMap);
            SelectSlot(Roughness, assignment.RoughnessMap);
            SelectSlot(Height, assignment.Height);
            return true;
        }

        private static void SelectSlot(MapSlot slot, string name)
        {
            Entry entry = string.IsNullOrEmpty(name) ? null : (EnsureEntry(name) ?? FindByName(AllImages, name));
            slot.Index = entry != null ? AllImages.IndexOf(entry) : -1;
            Plugin.Set(slot.Setting(), entry != null ? entry.Relative : "none");
        }

        // ---------------------------------------------------------------- finding things in the lists

        // An entry by its relative path, or by its bare file name (settings and saved crafts of older versions).
        private static int IndexOfKey(List<Entry> list, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return -1;
            }

            string normal = key.Replace('\\', '/');
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Relative, normal, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].FileName, normal, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static Entry FindByName(List<Entry> list, string key)
        {
            int index = IndexOfKey(list, key);
            return index >= 0 ? list[index] : null;
        }

        // "auto" = find it from the glTF material or by name next to the albedo image; "none" = no map; otherwise a path.
        private static void RestoreSlot(MapSlot slot)
        {
            slot.Index = -1;
            string wanted = slot.Setting().Value;
            if (string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase))
            {
                slot.Index = FindMap(slot);
                return;
            }

            slot.Index = IndexOfKey(AllImages, wanted);
        }

        // The slot follows the albedo image. What is saved is "auto", never "none": "none" is only for a map the user
        // switched off on purpose, otherwise stepping through the images would switch maps off for good.
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

        // Index (in AllImages) of the image for a slot: the one the glTF material names (when the model is a glTF whose base
        // color is the current albedo), else the albedo's set name plus one of the slot's endings, else a name that starts like
        // the set and says what it is.
        private static int FindMap(MapSlot slot)
        {
            Entry albedo = CurrentImage;
            if (albedo == null)
            {
                return -1;
            }

            if (_gltfFiles != null && slot.FromGltf != null && _gltfFiles.BaseColor != null
                && string.Equals(_gltfFiles.BaseColor, albedo.Relative, StringComparison.OrdinalIgnoreCase))
            {
                int fromGltf = IndexOfKey(AllImages, slot.FromGltf(_gltfFiles));
                if (fromGltf >= 0)
                {
                    return fromGltf;
                }
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

            // Names that do not follow the endings (B2BaseColor, B2Normal, B2Metallic-B2Roughness@channels=B): the file starts
            // with the set's name and its name says what it is.
            if (stem.Length >= 2)
            {
                string setLower = stem.ToLowerInvariant();
                for (int i = 0; i < AllImages.Count; i++)
                {
                    string lower = System.IO.Path.GetFileNameWithoutExtension(AllImages[i].FileName).ToLowerInvariant();
                    if (AllImages[i] != albedo && lower.StartsWith(setLower, StringComparison.Ordinal) && slot.NameFits(lower))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        // A map file of a set: its name is some albedo image's set name plus a map ending
        // (fw_190d_9_n_ao next to fw_190d_9_a). Images that merely end like a map but have no albedo next to them stay in the list.
        private static bool IsMapFileOfASet(Entry image)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(image.FileName);
            string lowerStem = stem.ToLowerInvariant();
            for (int s = 0; s < Slots.Length; s++)
            {
                if (Slots[s].NameFits(lowerStem) && HasAlbedoStartingTheName(lowerStem, image))
                {
                    return true;
                }

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

        // Some other image is an albedo (its name ends like one) and the given name starts with its set's name.
        private static bool HasAlbedoStartingTheName(string lowerName, Entry except)
        {
            for (int i = 0; i < AllImages.Count; i++)
            {
                if (AllImages[i] == except)
                {
                    continue;
                }

                string set = SetName(AllImages[i]);
                string stem = System.IO.Path.GetFileNameWithoutExtension(AllImages[i].FileName);
                if (set.Length >= 2 && set.Length < stem.Length && lowerName.StartsWith(set.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return true;
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
                        Plugin.SelectedModel.Value = Models[i].Relative;
                    }

                    UpdateGltf();
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- README, folder, loading

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
                    + "One FOLDER per aircraft: create a folder here (for example \"fw 190\" or \"b2\") and put in it the 3D model\r\n"
                    + "(.glb, .gltf or .obj) and its images (PNG or JPG). In the designer open the menu (the three-line button),\r\n"
                    + "choose Textures and pick the folder with Previous set / Next set. The mod finds the model, the colour image and\r\n"
                    + "the other maps by itself:\r\n"
                    + "- .glb / .gltf: the images of the material that covers most of the model are taken from the file itself (a copy\r\n"
                    + "  is written to the _cache folder). Export textures as PNG or JPEG, and without Draco or meshopt compression.\r\n"
                    + "- .obj: the images are found by name: for fw_190d_9_a.jpg the mod looks for fw_190d_9_n_n (normal), _n_ao\r\n"
                    + "  (occlusion), _n_s (specular); also _n, _nrm, _normal, _ao, _occlusion, _metallic, _roughness, _h, _bump, and names\r\n"
                    + "  like B2BaseColor / B2Normal / B2Metallic-B2Roughness@channels=B (blue = metallic) and @channels=G (green = roughness).\r\n"
                    + "  The OBJ must have UVs (vt lines). An image and an OBJ with the same name are paired.\r\n"
                    + "- Without a model the image is tiled over the procedural parts (fuselage, wings...).\r\n"
                    + "Files left directly in this folder still work: they form the set \"(files in the main folder)\".\r\n"
                    + "Anything the mod chose can be changed by hand in the panel.\r\n"
                    + "\r\n"
                    + "The SimplePlanes 2 part shader reads normal, occlusion and height maps and has one metallic and one smoothness\r\n"
                    + "value for the whole craft: metalness, roughness and specular images only give their average, unless you turn on\r\n"
                    + "\"Use the URP Lit shader\" in the panel (per pixel, experimental).\r\n"
                    + "\r\n"
                    + "When you apply a texture in the designer it is saved for the craft (by its name) in the Crafts folder next to this\r\n"
                    + "file, with the texture coordinates (.uv). Opening that craft in the designer or flying it puts the texture on\r\n"
                    + "again. Remove in the panel takes the texture off and forgets it. The craft file is never changed.\r\n"
                    + "An aircraft with moving parts (a swing wing, folding gear) must be in the SAME POSE AS THE MODEL when you press\r\n"
                    + "Apply (an F-14 model has its wings forward: put the wings forward, apply, then sweep them back and save the\r\n"
                    + "craft). The panel's \"Model fit\" row says POOR when the model does not match the craft.\r\n");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[TEX] could not write the README: " + ex.Message);
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
