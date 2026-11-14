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

        internal static ManualLogSource Log;

        internal static ConfigEntry<Key> DumpKey;
        internal static ConfigEntry<int> DumpMaxParts;

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

            Log.LogInfo(PluginName + " v" + Version + " loaded | Unity " + Application.unityVersion + " | game " + Application.version);

            CompatibilityCheck.Run();

            _harmony = new Harmony(PluginGuid);
            SafePatcher.PatchAll(_harmony, typeof(Plugin).Assembly);

            Log.LogInfo("Ready. Open a craft (designer or flight) and press " + DumpKey.Value + " to write the diagnostic dump.");
        }

        private void Update()
        {
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                Key key = DumpKey.Value;
                if (key != Key.None && keyboard[key].wasPressedThisFrame)
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
    }
}
