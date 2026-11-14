using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft.MeshGen;
using HarmonyLib;

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
}
