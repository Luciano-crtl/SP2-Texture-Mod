using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SP2TextureMod
{
    /// <summary>
    /// Entry point of the plugin: settings and the one optional hotkey.
    /// The user interface is a panel built with the game's own UI library (UI/), opened from a button in the
    /// designer's main menu. The work is done by AtlasApplier (apply/remove), TextureLibrary (the user's folder),
    /// ObjModel / TriangleBvh / ModelAlignment / UvTransfer (War Thunder model UVs) and
    /// DiagnosticDump (log dump of how the aircraft is rendered).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.sp2texturemod";
        public const string PluginName = "SP2 Texture Mod";

        // Shown in the log. If the log shows an older number, the old DLL is still in BepInEx\plugins.
        public const string Version = "0.1.0";

        // Which development phase this build contains (the version is only bumped when a whole set is done).
        public const string Phase = "3d";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // The only key. The game itself uses F9 (screenshot mode) and F10 (self-destruct): never use those.
        internal static ConfigEntry<Key> PanelKey;

        internal static ConfigEntry<string> SelectedTexture;
        internal static ConfigEntry<string> SelectedModel;
        internal static ConfigEntry<float> TilesPerMeter;
        internal static ConfigEntry<bool> FlipModelX;
        internal static ConfigEntry<string> SelectedNormal;
        internal static ConfigEntry<string> SelectedOcclusion;
        internal static ConfigEntry<string> SelectedHeight;
        internal static ConfigEntry<float> NormalStrength;
        internal static ConfigEntry<bool> FlipNormalGreen;
        internal static ConfigEntry<bool> RebuildNormalZ;
        internal static ConfigEntry<bool> HeightToNormal;
        internal static ConfigEntry<float> BumpStrength;
        internal static ConfigEntry<float> OcclusionStrength;
        internal static ConfigEntry<bool> UseParallax;
        internal static ConfigEntry<float> ParallaxHeight;
        internal static ConfigEntry<float> Metallic;
        internal static ConfigEntry<float> Smoothness;
        internal static ConfigEntry<bool> ProjectAlongNormals;
        internal static ConfigEntry<float> RayStart;
        internal static ConfigEntry<float> FacingWeight;
        internal static ConfigEntry<float> MaxMatchDistance;
        internal static ConfigEntry<float> AdjustOffsetX;
        internal static ConfigEntry<float> AdjustOffsetY;
        internal static ConfigEntry<float> AdjustOffsetZ;
        internal static ConfigEntry<float> AdjustRotationX;
        internal static ConfigEntry<float> AdjustRotationY;
        internal static ConfigEntry<float> AdjustRotationZ;
        internal static ConfigEntry<float> AdjustScale;
        internal static ConfigEntry<int> DumpMaxParts;

        private Harmony _harmony;
        private bool _updateErrorLogged;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            PanelKey = Config.Bind("Keys", "PanelKey", Key.Insert,
                "Opens or closes the Textures panel while a craft is open in the designer. The panel also has a button in the designer's main menu. Use None for no key at all.");

            SelectedTexture = Config.Bind("Texture", "SelectedTexture", "",
                "File name of the selected image (set by the panel).");
            SelectedModel = Config.Bind("Texture", "SelectedModel", "auto",
                "File name of the selected OBJ model; 'auto' = the one with the same name as the image, 'none' = tiled (set by the panel).");
            TilesPerMeter = Config.Bind("Texture", "TilesPerMeter", 1f,
                new ConfigDescription("How many times an image without a model repeats per meter of the aircraft.",
                    new AcceptableValueRange<float>(0.05f, 20f)));

            FlipModelX = Config.Bind("Model", "FlipModelX", true,
                "Mirror the OBJ on X when reading it, like most importers into Unity do. If the left and right sides of the texture come out swapped, set this to false.");
            SelectedNormal = Config.Bind("Maps", "SelectedNormal", "auto",
                "File name of the normal map; 'auto' = the one named like the albedo image with _n, _nrm, _normal... instead of _a; 'none' = no normal map (set by the panel).");
            SelectedOcclusion = Config.Bind("Maps", "SelectedOcclusion", "auto",
                "File name of the occlusion map; 'auto' = the one named like the albedo image with _ao, _occ, _occlusion...; 'none' = none (set by the panel).");
            SelectedHeight = Config.Bind("Maps", "SelectedHeight", "auto",
                "File name of the height (bump) map; 'auto' = the one named like the albedo image with _h, _height, _bump...; 'none' = none (set by the panel).");
            NormalStrength = Config.Bind("Maps", "NormalStrength", 1f,
                new ConfigDescription("How strong the normal map is (the shader's bump scale).", new AcceptableValueRange<float>(0f, 3f)));
            FlipNormalGreen = Config.Bind("Maps", "FlipNormalGreen", false,
                "Flip the green channel of the normal map. DirectX-style maps (many game textures) need it; if the relief looks inverted (bumps look like dents), turn it on.");
            RebuildNormalZ = Config.Bind("Maps", "RebuildNormalZ", true,
                "Rebuild the blue channel of the normal map from red and green. Many game textures keep other data in blue.");
            HeightToNormal = Config.Bind("Maps", "HeightToNormal", false,
                "With no normal map, make one from the height (bump) map.");
            BumpStrength = Config.Bind("Maps", "BumpStrength", 3f,
                new ConfigDescription("Strength used when a bump (height) image is converted to a normal map.", new AcceptableValueRange<float>(0.1f, 20f)));
            OcclusionStrength = Config.Bind("Maps", "OcclusionStrength", 1f,
                new ConfigDescription("How strong the occlusion map is.", new AcceptableValueRange<float>(0f, 1f)));
            UseParallax = Config.Bind("Maps", "UseParallax", false,
                "Use the height map for the parallax effect (the surface seems to have depth). Experimental: it can distort the texture.");
            ParallaxHeight = Config.Bind("Maps", "ParallaxHeight", 0.02f,
                new ConfigDescription("Depth of the parallax effect.", new AcceptableValueRange<float>(0.005f, 0.08f)));
            Metallic = Config.Bind("Material", "Metallic", 0f,
                new ConfigDescription("Metallic of the textured parts, one value for all of it (the part shader has no metallic map).", new AcceptableValueRange<float>(0f, 1f)));
            Smoothness = Config.Bind("Material", "Smoothness", 0f,
                new ConfigDescription("Smoothness of the textured parts, one value for all of it (the part shader has no smoothness map).", new AcceptableValueRange<float>(0f, 1f)));

            ProjectAlongNormals = Config.Bind("Model", "ProjectAlongNormals", true,
                "On: each point of the craft is matched to the first model surface met by a ray going inwards along its normal (the way textures are baked from a detailed model). Off: the closest model surface.");
            RayStart = Config.Bind("Model", "RayStart", 0.3f,
                new ConfigDescription("How far outside the craft's surface the rays start (meters). It must be more than the craft sticks out of the model at any point.",
                    new AcceptableValueRange<float>(0.02f, 3f)));
            FacingWeight = Config.Bind("Model", "FacingWeight", 0.03f,
                new ConfigDescription("Nearest-surface search and ray fallback: how strongly a point prefers model surfaces that face the same way (square meters). Keep it small: a large value picks far away surfaces.",
                    new AcceptableValueRange<float>(0f, 1f)));
            MaxMatchDistance = Config.Bind("Model", "MaxMatchDistance", 0.75f,
                new ConfigDescription("Vertices farther than this from the model are only counted in the log statistics (meters).",
                    new AcceptableValueRange<float>(0.01f, 50f)));

            AdjustOffsetX = Config.Bind("Model adjustment", "OffsetX", 0f, new ConfigDescription("Shift of the model after the automatic alignment, along X (meters).", new AcceptableValueRange<float>(-50f, 50f)));
            AdjustOffsetY = Config.Bind("Model adjustment", "OffsetY", 0f, new ConfigDescription("Shift of the model after the automatic alignment, along Y (meters).", new AcceptableValueRange<float>(-50f, 50f)));
            AdjustOffsetZ = Config.Bind("Model adjustment", "OffsetZ", 0f, new ConfigDescription("Shift of the model after the automatic alignment, along Z (meters).", new AcceptableValueRange<float>(-50f, 50f)));
            AdjustRotationX = Config.Bind("Model adjustment", "RotationX", 0f, new ConfigDescription("Extra rotation about X (degrees).", new AcceptableValueRange<float>(-180f, 180f)));
            AdjustRotationY = Config.Bind("Model adjustment", "RotationY", 0f, new ConfigDescription("Extra rotation about Y (degrees).", new AcceptableValueRange<float>(-180f, 180f)));
            AdjustRotationZ = Config.Bind("Model adjustment", "RotationZ", 0f, new ConfigDescription("Extra rotation about Z (degrees).", new AcceptableValueRange<float>(-180f, 180f)));
            AdjustScale = Config.Bind("Model adjustment", "Scale", 1f, new ConfigDescription("Extra scale of the model (1 = none).", new AcceptableValueRange<float>(0.5f, 2f)));

            DumpMaxParts = Config.Bind("Diagnostics", "DumpMaxParts", 60,
                new ConfigDescription("Maximum number of parts listed in detail by the diagnostic dump (panel > Tools).",
                    new AcceptableValueRange<int>(1, 2000)));

            Log.LogInfo(PluginName + " v" + Version + " (phase " + Phase + ") loaded | Unity " + Application.unityVersion + " | game " + Application.version);

            TextureLibrary.Refresh();
            Log.LogInfo("Texture folder: " + TextureLibrary.Folder + " (" + TextureLibrary.ImageCount + " image(s), "
                + TextureLibrary.ModelCount + " model(s) found)");

            CompatibilityCheck.Run();

            _harmony = new Harmony(PluginGuid);
            SafePatcher.PatchAll(_harmony, typeof(Plugin).Assembly);

            Log.LogInfo("Ready. In the designer open the main menu (three-line button) and choose Textures"
                + (PanelKey.Value != Key.None ? ", or press " + PanelKey.Value : "") + ".");
        }

        private void Update()
        {
            try
            {
                AtlasApplier.Tick();
                UI.TexturePanel.Tick();

                Key key = PanelKey.Value;
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null && key != Key.None && keyboard[key].wasPressedThisFrame)
                {
                    UI.TexturePanel.Toggle();
                }
            }
            catch (Exception ex)
            {
                // Never let the mod freeze or spam: report once.
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    Log.LogError("Update failed (reported once): " + ex);
                }
            }
        }
    }
}
