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

            try
            {
                FlightApplier.OnMeshRebuilt(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PATCH] ProceduralMeshPatch flight postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// In flight a group of parts merges the meshes of its parts into one when it initializes (the frame after the
    /// aircraft is activated). This runs just before that: the saved texture is put on the procedural parts of the
    /// group, which are marked "do not merge" so they keep their own renderer and material.
    /// </summary>
    [HarmonyPatch(typeof(PartGroupScript), "Initialize")]
    internal static class PartGroupInitializePatch
    {
        private static void Prefix(PartGroupScript __instance)
        {
            try
            {
                FlightApplier.OnGroupInitializing(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PATCH] PartGroupInitializePatch prefix failed: " + ex);
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
                AtlasMaterial.OnThemeMaterialUpdated(__instance, material);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PATCH] ThemeMaterialPatch postfix failed: " + ex.Message);
            }
        }
    }
}
