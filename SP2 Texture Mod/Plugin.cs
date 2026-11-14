using System;
using System.Globalization;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SP2TextureMod
{
    /// <summary>
    /// Entry point of the plugin: settings, hotkeys and the on-screen message.
    /// The work is done by AtlasApplier (apply/remove), TextureLibrary (the user's folder),
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
        public const string Phase = "3";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Keys. None of these is used by the game (the game uses F9 for screenshot mode and F10 for self-destruct).
        internal static ConfigEntry<Key> ApplyKey;
        internal static ConfigEntry<Key> NextTextureKey;
        internal static ConfigEntry<Key> PreviousTextureKey;
        internal static ConfigEntry<Key> TileUpKey;
        internal static ConfigEntry<Key> TileDownKey;
        internal static ConfigEntry<Key> DumpKey;

        internal static ConfigEntry<string> SelectedTexture;
        internal static ConfigEntry<float> TilesPerMeter;
        internal static ConfigEntry<bool> FlipModelX;
        internal static ConfigEntry<float> NormalWeight;
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
        private string _overlayText = "";
        private float _overlayUntil;
        private GUIStyle _overlayStyle;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ApplyKey = Config.Bind("Keys", "ApplyKey", Key.Insert,
                "Applies the selected image to the aircraft open in the designer, and removes it on the next press.");
            NextTextureKey = Config.Bind("Keys", "NextTextureKey", Key.PageDown, "Selects the next image of the texture folder.");
            PreviousTextureKey = Config.Bind("Keys", "PreviousTextureKey", Key.PageUp, "Selects the previous image of the texture folder.");
            TileUpKey = Config.Bind("Keys", "TileUpKey", Key.Home, "More repetitions per meter (only for images tiled without a model).");
            TileDownKey = Config.Bind("Keys", "TileDownKey", Key.End, "Fewer repetitions per meter (only for images tiled without a model).");
            DumpKey = Config.Bind("Keys", "DumpKey", Key.Pause,
                "Writes a rendering diagnostic of the current aircraft to BepInEx/LogOutput.log. Use None to disable.");

            SelectedTexture = Config.Bind("Texture", "SelectedTexture", "",
                "File name of the selected image (changed by the keys).");
            TilesPerMeter = Config.Bind("Texture", "TilesPerMeter", 1f,
                new ConfigDescription("How many times an image without a model repeats per meter of the aircraft.",
                    new AcceptableValueRange<float>(0.05f, 20f)));

            FlipModelX = Config.Bind("Model", "FlipModelX", true,
                "Mirror the OBJ on X when reading it, like most importers into Unity do. If the left and right sides of the texture come out swapped, set this to false.");
            NormalWeight = Config.Bind("Model", "NormalWeight", 0.25f,
                new ConfigDescription("How strongly a vertex prefers model surfaces that face the same way (square meters). Raise it if a wing top picks the bottom of the model.",
                    new AcceptableValueRange<float>(0f, 10f)));
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
                new ConfigDescription("Maximum number of parts listed in detail by the diagnostic dump.",
                    new AcceptableValueRange<int>(1, 2000)));

            Log.LogInfo(PluginName + " v" + Version + " (phase " + Phase + ") loaded | Unity " + Application.unityVersion + " | game " + Application.version);

            TextureLibrary.Refresh();
            Log.LogInfo("Texture folder: " + TextureLibrary.Folder + " (" + TextureLibrary.Count + " PNG/JPG image(s) found)");

            CompatibilityCheck.Run();

            _harmony = new Harmony(PluginGuid);
            SafePatcher.PatchAll(_harmony, typeof(Plugin).Assembly);

            Log.LogInfo("Ready. Open a craft in the designer: " + ApplyKey.Value + " = apply/remove, "
                + PreviousTextureKey.Value + "/" + NextTextureKey.Value + " = choose image, "
                + TileDownKey.Value + "/" + TileUpKey.Value + " = tiling, " + DumpKey.Value + " = diagnostic dump.");
        }

        /// <summary>Re-reads the .cfg so edits made while the game is running are used on the next apply.</summary>
        internal void ReloadConfig()
        {
            try
            {
                Config.Reload();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not reload the settings file: " + ex.Message);
            }
        }

        private void Update()
        {
            try
            {
                AtlasApplier.Tick();

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                if (Pressed(keyboard, ApplyKey))
                {
                    TextureLibrary.Refresh(); // picks up images copied into the folder meanwhile
                    AtlasApplier.ToggleCurrent();
                    ShowOverlay();
                }

                if (Pressed(keyboard, NextTextureKey))
                {
                    TextureLibrary.Step(1);
                    AtlasApplier.ReapplyIfActive();
                    ShowOverlay();
                }

                if (Pressed(keyboard, PreviousTextureKey))
                {
                    TextureLibrary.Step(-1);
                    AtlasApplier.ReapplyIfActive();
                    ShowOverlay();
                }

                if (Pressed(keyboard, TileUpKey))
                {
                    TilesPerMeter.Value = Mathf.Clamp(TilesPerMeter.Value * 1.25f, 0.05f, 20f);
                    AtlasApplier.ReapplyIfActive();
                    ShowOverlay();
                }

                if (Pressed(keyboard, TileDownKey))
                {
                    TilesPerMeter.Value = Mathf.Clamp(TilesPerMeter.Value * 0.8f, 0.05f, 20f);
                    AtlasApplier.ReapplyIfActive();
                    ShowOverlay();
                }

                if (Pressed(keyboard, DumpKey))
                {
                    DiagnosticDump.Dump();
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

        private static bool Pressed(Keyboard keyboard, ConfigEntry<Key> entry)
        {
            Key key = entry.Value;
            return key != Key.None && keyboard[key].wasPressedThisFrame;
        }

        // ------------------------------------------------------------------ on-screen message (display only, no clicks)

        private void ShowOverlay()
        {
            _overlayText = BuildOverlayText();
            _overlayUntil = Time.unscaledTime + 10f;
        }

        private static string BuildOverlayText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(PluginName).Append(" v").Append(Version).Append(" (phase ").Append(Phase).Append(")   ")
              .Append(ApplyKey.Value).Append(" = apply/remove   ")
              .Append(PreviousTextureKey.Value).Append("/").Append(NextTextureKey.Value).Append(" = image   ")
              .Append(TileDownKey.Value).Append("/").Append(TileUpKey.Value).Append(" = tiling\n");

            TextureLibrary.Entry entry = TextureLibrary.Current;
            if (entry == null)
            {
                sb.Append("No PNG/JPG found, using a built-in test pattern. Put images in: ").Append(TextureLibrary.Folder).Append('\n');
            }
            else
            {
                sb.Append("Image ").Append(TextureLibrary.Index + 1).Append("/").Append(TextureLibrary.Count).Append(": ").Append(entry.FileName);
                if (entry.ModelFilePath != null)
                {
                    sb.Append("   + model ").Append(System.IO.Path.GetFileName(entry.ModelFilePath));
                }
                else
                {
                    sb.Append("   (tiled: ").Append(TilesPerMeter.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append(" per meter)");
                }

                sb.Append('\n');
            }

            sb.Append(AtlasApplier.StatusText);
            return sb.ToString();
        }

        private void OnGUI()
        {
            if (Time.unscaledTime > _overlayUntil || _overlayText.Length == 0)
            {
                return;
            }

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
                _overlayStyle.fontSize = 14;
                _overlayStyle.wordWrap = true;
                _overlayStyle.normal.textColor = Color.white;
            }

            float width = Mathf.Min(980f, Screen.width - 24f);
            Rect box = new Rect(12f, Screen.height - 170f, width, 158f);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(box.x + 8f, box.y + 4f, box.width - 16f, box.height - 8f), _overlayText, _overlayStyle);
        }
    }
}
