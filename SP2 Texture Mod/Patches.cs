using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.MeshGen;
using HarmonyLib;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// After a procedural part (fuselage, wing...) rebuilds its mesh the game has rewritten UV0 and UV1,
    /// so the texture is applied again. Both UpdateMesh overloads are patched.
    /// </summary>
    [HarmonyPatch]
    internal static class ProceduralMeshPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(ProceduralPartMeshRenderer).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.Name == "UpdateMesh")
                {
                    yield return method;
                }
            }
        }

        private static void Postfix(ProceduralPartMeshRenderer __instance)
        {
            try
            {
                AtlasApplier.OnMeshRebuilt(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PATCH] ProceduralMeshPatch postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The theme writes _MaterialData into every material it keeps each time the paint changes (the one-argument
    /// overload is the one that does it per material). The atlas material gets its metallic and smoothness back.
    /// </summary>
    [HarmonyPatch(typeof(ThemeScript), "UpdateMaterialProperties", new Type[] { typeof(Material) })]
    internal static class ThemeMaterialPatch
    {
        private static void Postfix(ThemeScript __instance, Material material)
        {
            try
            {
                AtlasApplier.OnThemeMaterialUpdated(__instance, material);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PATCH] ThemeMaterialPatch postfix failed: " + ex.Message);
            }
        }
    }
}
