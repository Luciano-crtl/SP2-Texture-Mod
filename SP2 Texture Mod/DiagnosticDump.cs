using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Paint;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace SP2TextureMod
{
    /// <summary>
    /// Phase 1 diagnostic: writes to the log how the current aircraft is rendered.
    /// It answers the open questions before any texture code is written:
    ///  - do the procedural part meshes have usable UV0 (or is it all zeros)?
    ///  - which shader and properties does the part material use?
    ///  - which negative ids do the reserved "Albedo" paint styles have?
    ///  - which renderers are excluded from mesh combining?
    /// Read-only: it changes nothing in the game.
    /// </summary>
    internal static class DiagnosticDump
    {
        private const string Tag = "[DIAG] ";

        private enum Uv0State
        {
            NoMesh = 0,
            NoAttribute = 1,
            Unreadable = 2,
            AllZero = 3,
            HasData = 4
        }

        public static void Dump()
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            string context;
            AircraftScript aircraft = FindAircraft(out context);
            if (aircraft == null)
            {
                Plugin.Log.LogWarning(Tag + "no aircraft found. Open a craft in the designer or start a flight, then press the key again.");
                return;
            }

            Plugin.Log.LogInfo(Tag + "===== dump begin | mod v" + Plugin.Version + " phase " + Plugin.Phase + " | context=" + context + " =====");

            HashSet<string> seenShaders = new HashSet<string>();
            try { DumpAircraft(aircraft); }
            catch (Exception ex) { Plugin.Log.LogError(Tag + "aircraft section failed: " + ex); }

            try { DumpTheme(aircraft, seenShaders); }
            catch (Exception ex) { Plugin.Log.LogError(Tag + "theme section failed: " + ex); }

            try { DumpParts(aircraft, seenShaders); }
            catch (Exception ex) { Plugin.Log.LogError(Tag + "parts section failed: " + ex); }

            Plugin.Log.LogInfo(Tag + "===== dump end (" + watch.ElapsedMilliseconds + " ms) =====");
        }

        // ------------------------------------------------------------------ finding the aircraft

        internal static AircraftScript FindAircraft(out string context)
        {
            context = "none";

            try
            {
                Assets.Scripts.Flight.FlightSceneScript flight = Assets.Scripts.Flight.FlightSceneScript.Instance;
                if (flight != null && flight.LocalPlayer != null && flight.LocalPlayer.Aircraft != null)
                {
                    context = "Flight";
                    return flight.LocalPlayer.Aircraft;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug(Tag + "flight lookup failed: " + ex.Message);
            }

            try
            {
                Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
                if (designer != null && designer.Aircraft != null)
                {
                    context = "Designer";
                    return designer.Aircraft;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug(Tag + "designer lookup failed: " + ex.Message);
            }

            return null;
        }

        // ------------------------------------------------------------------ aircraft

        private static void DumpAircraft(AircraftScript aircraft)
        {
            int partCount = aircraft.Parts != null ? aircraft.Parts.Count : -1;

            string bounds;
            try
            {
                Bounds b = aircraft.CalculateBounds(false);
                bounds = "center=" + F(b.center) + " size=" + F(b.size);
            }
            catch (Exception ex)
            {
                bounds = "error: " + ex.Message;
            }

            Transform t = aircraft.transform;
            Plugin.Log.LogInfo(Tag + "Aircraft: parts=" + partCount
                + " loadContext=" + aircraft.LoadContext
                + " position=" + F(t.position)
                + " euler=" + F(t.eulerAngles)
                + " scale=" + F(t.lossyScale)
                + " bounds{" + bounds + "}");
        }

        // ------------------------------------------------------------------ theme and shader

        private static void DumpTheme(AircraftScript aircraft, HashSet<string> seenShaders)
        {
            ThemeScript themeScript = aircraft.Theme;
            if (themeScript == null || themeScript.Theme == null)
            {
                Plugin.Log.LogWarning(Tag + "aircraft has no theme");
                return;
            }

            ThemeData theme = themeScript.Theme;

            Dictionary<string, int> styles = new Dictionary<string, int>();
            foreach (PartMaterial material in theme.AllMaterials)
            {
                string key = material.Style.ToString();
                int count;
                styles.TryGetValue(key, out count);
                styles[key] = count + 1;
            }

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in styles)
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(pair.Key).Append('=').Append(pair.Value);
            }

            Plugin.Log.LogInfo(Tag + "Theme '" + theme.Name + "': " + theme.AllMaterials.Count + " materials (" + sb + ")");

            LogReservedMaterial(theme, PaintStyle.AlbedoTexture);
            LogReservedMaterial(theme, PaintStyle.AlbedoTextureSupersampledWithMipmapBias);

            DumpShaderOnce(themeScript.Material, "ThemeScript.Material (shared part material)", seenShaders);
        }

        private static void LogReservedMaterial(ThemeData theme, PaintStyle style)
        {
            try
            {
                PartMaterial reserved = theme.GetReservedMaterial(style);
                int index = theme.GetMaterialIndex(reserved.Id);
                Plugin.Log.LogInfo(Tag + "reserved material " + style + ": id=" + reserved.Id
                    + " shaderIndex=" + index + " styleValue=" + (int)reserved.Style);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "reserved material " + style + " not available: " + ex.Message);
            }
        }

        private static void DumpShaderOnce(Material material, string label, HashSet<string> seenShaders)
        {
            if (material == null || material.shader == null)
            {
                return;
            }

            Shader shader = material.shader;
            if (!seenShaders.Add(shader.name))
            {
                return;
            }

            StringBuilder props = new StringBuilder();
            int propertyCount = shader.GetPropertyCount();
            for (int i = 0; i < propertyCount; i++)
            {
                if (i > 0)
                {
                    props.Append("  ");
                }

                props.Append(shader.GetPropertyName(i)).Append(':').Append(shader.GetPropertyType(i));
            }

            Plugin.Log.LogInfo(Tag + "Shader '" + shader.name + "' (first seen on " + label + ", material '" + material.name
                + "', renderQueue=" + material.renderQueue + ") properties[" + propertyCount + "]: " + props);

            try
            {
                LocalKeyword[] keywords = material.enabledKeywords;
                StringBuilder kb = new StringBuilder();
                for (int i = 0; i < keywords.Length; i++)
                {
                    if (i > 0)
                    {
                        kb.Append(' ');
                    }

                    kb.Append(keywords[i].name);
                }

                Plugin.Log.LogInfo(Tag + "  enabled keywords on '" + material.name + "': " + kb);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug(Tag + "  could not read keywords: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ parts and renderers

        private static void DumpParts(AircraftScript aircraft, HashSet<string> seenShaders)
        {
            if (aircraft.Parts == null)
            {
                Plugin.Log.LogWarning(Tag + "aircraft has no part list");
                return;
            }

            int maxDetailed = Plugin.DumpMaxParts.Value;
            int detailed = 0;
            int parts = 0;
            int renderers = 0;
            int excluded = 0;
            long vertices = 0;
            int[] uvStates = new int[5];
            Dictionary<string, int> byType = new Dictionary<string, int>();

            foreach (PartData data in aircraft.Parts)
            {
                if (data == null || data.PartScript == null)
                {
                    continue;
                }

                parts++;
                PartScript script = data.PartScript;
                string typeName = data.PartType != null ? data.PartType.Name : "?";

                int typeCount;
                byType.TryGetValue(typeName, out typeCount);
                byType[typeName] = typeCount + 1;

                List<PartMaterialScript.RendererMaterialMap> maps =
                    script.PartMaterialScript != null ? script.PartMaterialScript.RendererMaps : null;

                bool detail = detailed < maxDetailed;
                if (detail)
                {
                    detailed++;
                    string materialIds = data.MaterialIds != null ? string.Join(",", data.MaterialIds) : "";
                    Plugin.Log.LogInfo(Tag + "Part #" + data.Id + " '" + data.Name + "' type=" + typeName
                        + " combine=" + (data.PartType != null && data.PartType.CombineMeshes)
                        + " materialIds=[" + materialIds + "]"
                        + " renderers=" + (maps != null ? maps.Count : 0));
                }

                if (maps == null)
                {
                    continue;
                }

                for (int i = 0; i < maps.Count; i++)
                {
                    PartMaterialScript.RendererMaterialMap map = maps[i];
                    Renderer renderer = map.Renderer;
                    if (renderer == null)
                    {
                        continue;
                    }

                    renderers++;
                    if (map.ExcludeFromMeshCombine)
                    {
                        excluded++;
                    }

                    Mesh mesh = null;
                    try { mesh = map.Mesh; }
                    catch (Exception) { }

                    Uv0State state;
                    string meshText = DescribeMesh(mesh, out state);
                    uvStates[(int)state]++;
                    if (mesh != null)
                    {
                        vertices += mesh.vertexCount;
                    }

                    // Every distinct shader is listed once, even for parts that are not printed in detail.
                    DumpShaderOnce(renderer.sharedMaterial, "renderer '" + renderer.name + "' of part '" + data.Name + "'", seenShaders);

                    if (detail)
                    {
                        Plugin.Log.LogInfo(Tag + "  R" + i + " '" + renderer.name + "' " + meshText
                            + " excludeCombine=" + map.ExcludeFromMeshCombine
                            + " uniqueMesh=" + map.MeshIsUnique
                            + " enabled=" + renderer.enabled
                            + " layer=" + renderer.gameObject.layer
                            + " materials=" + DescribeMaterials(renderer, map));
                    }
                }
            }

            Plugin.Log.LogInfo(Tag + "Summary: parts=" + parts + " renderers=" + renderers + " vertices=" + vertices
                + " excludedFromCombine=" + excluded
                + " | uv0: allZero=" + uvStates[(int)Uv0State.AllZero]
                + " hasData=" + uvStates[(int)Uv0State.HasData]
                + " noAttribute=" + uvStates[(int)Uv0State.NoAttribute]
                + " unreadable=" + uvStates[(int)Uv0State.Unreadable]
                + " noMesh=" + uvStates[(int)Uv0State.NoMesh]);

            List<KeyValuePair<string, int>> types = new List<KeyValuePair<string, int>>(byType);
            types.Sort((a, b) => b.Value.CompareTo(a.Value));
            StringBuilder typeText = new StringBuilder();
            for (int i = 0; i < types.Count && i < 20; i++)
            {
                if (i > 0)
                {
                    typeText.Append(", ");
                }

                typeText.Append(types[i].Key).Append(" x").Append(types[i].Value);
            }

            Plugin.Log.LogInfo(Tag + "Part types: " + typeText);

            if (parts > detailed)
            {
                Plugin.Log.LogInfo(Tag + "(" + (parts - detailed) + " more part(s) not listed in detail; raise DumpMaxParts in BepInEx/config/" + Plugin.PluginGuid + ".cfg)");
            }
        }

        private static string DescribeMaterials(Renderer renderer, PartMaterialScript.RendererMaterialMap map)
        {
            Material[] materials = renderer.sharedMaterials;
            StringBuilder sb = new StringBuilder();
            for (int j = 0; j < materials.Length; j++)
            {
                if (j > 0)
                {
                    sb.Append(" | ");
                }

                Material m = materials[j];
                string type = (map.MaterialTypes != null && j < map.MaterialTypes.Length) ? map.MaterialTypes[j].ToString() : "?";
                sb.Append('[').Append(j).Append("] ")
                  .Append(m != null ? m.name : "null")
                  .Append(" / ")
                  .Append(m != null && m.shader != null ? m.shader.name : "-")
                  .Append(" / ")
                  .Append(type);
            }

            return sb.ToString();
        }

        private static string DescribeMesh(Mesh mesh, out Uv0State state)
        {
            if (mesh == null)
            {
                state = Uv0State.NoMesh;
                return "mesh=null";
            }

            string head = "mesh='" + mesh.name + "' verts=" + mesh.vertexCount + " submeshes=" + mesh.subMeshCount;

            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                state = Uv0State.NoAttribute;
                return head + " uv0=none";
            }

            if (!mesh.isReadable)
            {
                state = Uv0State.Unreadable;
                return head + " uv0=present(mesh not readable)";
            }

            List<Vector2> uv0 = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(0, uv0);

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < uv0.Count; i++)
            {
                min = Vector2.Min(min, uv0[i]);
                max = Vector2.Max(max, uv0[i]);
            }

            bool allZero = uv0.Count == 0
                || ((max - min).sqrMagnitude < 1e-12f && min.sqrMagnitude < 1e-12f);
            state = allZero ? Uv0State.AllZero : Uv0State.HasData;

            string uv0Text = allZero ? " uv0=all zero" : " uv0=[" + F(min) + " .. " + F(max) + "]";

            // UV1 holds (materialIndex, decalLayer, partId) per vertex in this game: list the material indexes.
            string uv1Text = "";
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                List<Vector3> uv1 = new List<Vector3>(mesh.vertexCount);
                mesh.GetUVs(1, uv1);
                HashSet<int> indexes = new HashSet<int>();
                for (int i = 0; i < uv1.Count && indexes.Count < 8; i++)
                {
                    indexes.Add(Mathf.RoundToInt(uv1[i].x));
                }

                uv1Text = " uv1.x(materialIndex)={" + string.Join(",", indexes) + "}";
            }

            return head + uv0Text + uv1Text;
        }

        // ------------------------------------------------------------------ formatting (always invariant culture)

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string F(Vector2 v)
        {
            return "(" + F(v.x) + ", " + F(v.y) + ")";
        }

        private static string F(Vector3 v)
        {
            return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
        }
    }
}
