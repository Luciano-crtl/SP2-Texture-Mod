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
    /// Entry point of the plugin.
    /// Phase 1: loads, writes its version to the log, checks the game's API (CompatibilityCheck)
    /// and offers a diagnostic dump (hotkey) of how the current aircraft is rendered.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.sp2texturemod";
        public const string PluginName = "SP2 Texture Mod";

        // Shown in the log. If the log shows an older number, the old DLL is still in BepInEx\plugins.
        public const string Version = "0.1.0";

        // Which development phase this build contains (the version is only bumped when a whole set is done).
        public const string Phase = "2";

        internal static ManualLogSource Log;

        internal static ConfigEntry<Key> DumpKey;
        internal static ConfigEntry<int> DumpMaxParts;
        internal static ConfigEntry<Key> TestKey;
        internal static ConfigEntry<float> TestTilesPerMeter;
        internal static ConfigEntry<bool> SupersampledStyle;

        private Harmony _harmony;
        private bool _updateErrorLogged;

        private void Awake()
        {
            Log = Logger;

            DumpKey = Config.Bind("Diagnostics", "DumpKey", Key.F9,
                "Key that writes a rendering diagnostic of the current aircraft to BepInEx/LogOutput.log. Use None to disable.");
            DumpMaxParts = Config.Bind("Diagnostics", "DumpMaxParts", 60,
                new ConfigDescription("Maximum number of parts listed in detail by the diagnostic dump.",
                    new AcceptableValueRange<int>(1, 2000)));

            TestKey = Config.Bind("Test texture", "TestKey", Key.F10,
                "Key that puts a generated checker texture on the procedural parts of the current aircraft and removes it on the second press. Use None to disable.");
            TestTilesPerMeter = Config.Bind("Test texture", "TilesPerMeter", 1f,
                new ConfigDescription("How many times the 8x8 checker repeats per meter of the aircraft.",
                    new AcceptableValueRange<float>(0.05f, 20f)));
            SupersampledStyle = Config.Bind("Test texture", "SupersampledStyle", false,
                "Use the game's AlbedoTextureSupersampledWithMipmapBias paint style instead of AlbedoTexture. Try it if the plain style shows a blank or wrong color.");

            Log.LogInfo(PluginName + " v" + Version + " (phase " + Phase + ") loaded | Unity " + Application.unityVersion + " | game " + Application.version);

            CompatibilityCheck.Run();

            _harmony = new Harmony(PluginGuid);
            SafePatcher.PatchAll(_harmony, typeof(Plugin).Assembly);

            Log.LogInfo("Ready. Open a craft: " + DumpKey.Value + " = diagnostic dump, " + TestKey.Value + " = test texture on/off.");
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

                Key dumpKey = DumpKey.Value;
                if (dumpKey != Key.None && keyboard[dumpKey].wasPressedThisFrame)
                {
                    DiagnosticDump.Dump();
                }

                Key testKey = TestKey.Value;
                if (testKey != Key.None && keyboard[testKey].wasPressedThisFrame)
                {
                    AtlasApplier.ToggleCurrent();
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
