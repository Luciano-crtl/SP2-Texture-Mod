using System;
using HarmonyLib;

namespace SP2TextureMod
{
    /// <summary>
    /// Applies Harmony patches class by class, so one failing patch never brings down the others.
    /// Each skipped patch is logged with the [PATCH] prefix.
    /// </summary>
    internal static class SafePatcher
    {
        public static void PatchAll(Harmony harmony, System.Reflection.Assembly assembly)
        {
            int applied = 0;
            int skipped = 0;

            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                {
                    continue;
                }

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                    Plugin.Log.LogInfo("[PATCH] " + type.Name + " applied");
                }
                catch (Exception ex)
                {
                    skipped++;
                    Plugin.Log.LogWarning("[PATCH] " + type.Name + " skipped: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Plugin.Log.LogInfo("[PATCH] " + applied + " applied, " + skipped + " skipped");
        }
    }
}
