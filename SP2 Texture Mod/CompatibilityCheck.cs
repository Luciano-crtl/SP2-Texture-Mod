using System;
using System.Reflection;

namespace SP2TextureMod
{
    /// <summary>
    /// Verifies, by reflection, that every game member this mod relies on (now or in the next phases)
    /// still exists. A game update can rename or retype members without warning; every problem is
    /// written to the log with the [COMPAT] prefix so the user can report it.
    /// Names come from reading the decompiled game code; nothing here was run in the game yet.
    /// </summary>
    internal static class CompatibilityCheck
    {
        private const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static System.Reflection.Assembly _game;
        private static int _checks;
        private static int _problems;

        public static void Run()
        {
            _checks = 0;
            _problems = 0;

            try
            {
                _game = typeof(Assets.Scripts.Craft.AircraftScript).Assembly;
                System.Reflection.AssemblyName name = _game.GetName();
                Plugin.Log.LogInfo("[COMPAT] game assembly " + name.Name + " " + name.Version);

                // --- Where part meshes are (re)built: UVs will be written after these calls ---
                const string meshRenderer = "Assets.Scripts.Craft.MeshGen.ProceduralPartMeshRenderer";
                Overloads(meshRenderer, "UpdateMesh", 2);
                Has(meshRenderer, "_partScript");
                Has(meshRenderer, "_mesh");
                Has(meshRenderer, "_renderer");
                Has(meshRenderer, "MeshRenderer");
                Has(meshRenderer, "ExcludeFromCombine");
                TypeExists("Assets.Scripts.Craft.MeshGen.PartMeshVertexUVs");

                // --- Theme / materials ---
                const string themeScript = "Assets.Scripts.Craft.ThemeScript";
                Has(themeScript, "Material");
                Has(themeScript, "Theme");
                Has(themeScript, "RequestTexturedPartMaterial");
                Has(themeScript, "RequestDefaultPartMaterialInstance");
                Has(themeScript, "IsTexturedPartMaterial");
                Has(themeScript, "UpdateMaterialProperties");
                Has(themeScript, "_texturedPartMaterialReferences");
                Has(themeScript, "_texturedPartMaterials");
                Has(themeScript, "_defaultPartMaterialInstances");

                const string themeData = "Assets.Scripts.Craft.ThemeData";
                Has(themeData, "AllMaterials");
                Has(themeData, "GetReservedMaterial");
                Has(themeData, "GetMaterialIndex");

                Has("Assets.Scripts.Craft.PartMaterial", "Style");
                Has("Assets.Scripts.Craft.PartMaterial", "Id");

                EnumValue("Assets.Scripts.Craft.Paint.PaintStyle", "AlbedoTexture");
                EnumValue("Assets.Scripts.Craft.Paint.PaintStyle", "AlbedoTextureSupersampledWithMipmapBias");
                EnumValue("Assets.Scripts.Craft.Parts.PartRendererMaterialType", "DefaultInstanced");

                // --- Per-part material script and its renderer maps ---
                const string materialScript = "Assets.Scripts.Craft.Parts.PartMaterialScript";
                Has(materialScript, "RendererMaps");
                Has(materialScript, "ApplyReservedPaintStyle");
                Has(materialScript, "SetMaterialNoEvents");
                Has(materialScript, "RestoreOriginalMaterials");
                Has(materialScript, "ApplyAllMaterials");
                Has(materialScript, "UpdateRenderers");
                Has(materialScript, "OverrideMaterial");
                Has(materialScript, "CustomMaterial");

                const string rendererMap = "Assets.Scripts.Craft.Parts.PartMaterialScript+RendererMaterialMap";
                Has(rendererMap, "Renderer");
                Has(rendererMap, "Mesh");
                Has(rendererMap, "OriginalMaterials");
                Has(rendererMap, "MaterialTypes");
                Has(rendererMap, "ExcludeFromMeshCombine");
                Has(rendererMap, "MeshIsUnique");
                Has(rendererMap, "SubmeshToLevelMap");
                Has(rendererMap, "SetRendererMaterial");

                // --- Mesh combining in flight ---
                const string groupScript = "Assets.Scripts.Craft.PartGroupScript";
                Has(groupScript, "CombineMeshes");
                Has(groupScript, "SurveyPartMeshes");
                Has(groupScript, "GetTexturedMaterial");

                // --- Parts and aircraft ---
                Has("Assets.Scripts.Craft.Parts.PartScript", "Part");
                Has("Assets.Scripts.Craft.Parts.PartScript", "PartMaterialScript");
                Has("Assets.Scripts.Craft.Parts.PartScript", "Aircraft");

                const string partData = "Assets.Scripts.Craft.Parts.PartData";
                Has(partData, "PartScript");
                Has(partData, "MaterialIds");
                Has(partData, "Name");
                Has(partData, "Id");
                Has(partData, "PartType");

                const string aircraft = "Assets.Scripts.Craft.AircraftScript";
                Has(aircraft, "Parts");
                Has(aircraft, "Theme");
                Has(aircraft, "LoadContext");
                Has(aircraft, "CalculateBounds");

                // --- How the mod finds the current aircraft ---
                Has("Assets.Scripts.Flight.FlightSceneScript", "Instance");
                Has("Assets.Scripts.Flight.FlightSceneScript", "LocalPlayer");
                Has("Assets.Scripts.Flight.FlightScenePlayer", "Aircraft");
                Has("Assets.Scripts.Design.Designer", "Instance");
                Has("Assets.Scripts.Design.Designer", "Aircraft");
                Has("Assets.Scripts.Design.Designer", "SelectedPart");
                Has("Assets.Scripts.Design.Designer", "CreateUndoStepForSelectedPart");
            }
            catch (Exception ex)
            {
                Problem("compatibility check crashed: " + ex);
            }

            if (_problems == 0)
            {
                Plugin.Log.LogInfo("[COMPAT] " + _checks + " checks, all OK");
            }
            else
            {
                Plugin.Log.LogWarning("[COMPAT] " + _checks + " checks, " + _problems
                    + " problem(s). The game probably changed since this mod was written: send this log.");
            }
        }

        private static void Problem(string text)
        {
            _problems++;
            Plugin.Log.LogWarning("[COMPAT] " + text);
        }

        private static Type FindType(string typeName)
        {
            Type type = _game.GetType(typeName, false);
            if (type == null)
            {
                Problem("type not found: " + typeName);
            }

            return type;
        }

        private static void TypeExists(string typeName)
        {
            _checks++;
            FindType(typeName);
        }

        // The member can be a method, property or field, public or not.
        private static void Has(string typeName, string memberName)
        {
            _checks++;
            Type type = FindType(typeName);
            if (type == null)
            {
                return;
            }

            if (type.GetMember(memberName, AllMembers).Length == 0)
            {
                Problem("member not found: " + typeName + "." + memberName);
            }
        }

        private static void Overloads(string typeName, string methodName, int minimum)
        {
            _checks++;
            Type type = FindType(typeName);
            if (type == null)
            {
                return;
            }

            int count = 0;
            foreach (MethodInfo method in type.GetMethods(AllMembers))
            {
                if (method.Name == methodName)
                {
                    count++;
                }
            }

            if (count < minimum)
            {
                Problem("expected at least " + minimum + " overload(s) of " + typeName + "." + methodName + " but found " + count);
            }
        }

        private static void EnumValue(string typeName, string valueName)
        {
            _checks++;
            Type type = FindType(typeName);
            if (type == null)
            {
                return;
            }

            if (!type.IsEnum || Array.IndexOf(Enum.GetNames(type), valueName) < 0)
            {
                Problem("enum value not found: " + typeName + "." + valueName);
            }
        }
    }
}
