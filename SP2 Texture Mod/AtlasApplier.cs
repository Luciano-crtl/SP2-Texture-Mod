using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Paint;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.MeshGen;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Phase 2: puts one texture on the procedural parts (fuselage, wings...) of the current aircraft,
    /// reversibly, to prove the whole path before the real atlas is used:
    ///   1. write UV0 (procedural meshes have none),
    ///   2. point UV1.x at the game's reserved "AlbedoTexture" paint style,
    ///   3. give the renderers a clone of the part material whose _BaseMap is the texture.
    /// Nothing is saved in the craft file: MaterialIds are never touched.
    /// The same key reverts everything.
    /// </summary>
    internal static class AtlasApplier
    {
        private const string Tag = "[TEST] ";

        private sealed class RendererState
        {
            public PartMaterialScript.RendererMaterialMap Map;
            public Material[] OriginalMaterials;
            public Material[] OriginalHighlight;
            public Material[] OriginalSelected;
            public bool OriginalExcludeFromCombine;
        }

        private sealed class PartState
        {
            public PartScript Part;
            public PartMaterialScript Materials;
            public AircraftScript Aircraft;
            public readonly Dictionary<Renderer, RendererState> Renderers = new Dictionary<Renderer, RendererState>();
        }

        private static readonly Dictionary<PartScript, PartState> Parts = new Dictionary<PartScript, PartState>();
        private static readonly Dictionary<ThemeScript, Material> AtlasMaterials = new Dictionary<ThemeScript, Material>();

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int SupersampleBiasId = Shader.PropertyToID("_SupersampleMipmapBias");

        private static Texture2D _texture;
        private static AircraftScript _activeAircraft;

        private static PaintStyle Style
        {
            get
            {
                return Plugin.SupersampledStyle.Value
                    ? PaintStyle.AlbedoTextureSupersampledWithMipmapBias
                    : PaintStyle.AlbedoTexture;
            }
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Hotkey: applies the test texture to the current aircraft, or reverts it if already applied.</summary>
        public static void ToggleCurrent()
        {
            string context;
            AircraftScript aircraft = DiagnosticDump.FindAircraft(out context);
            if (aircraft == null)
            {
                Plugin.Log.LogWarning(Tag + "no aircraft found. Open a craft in the designer first.");
                return;
            }

            Prune();
            if (Parts.Count > 0)
            {
                RevertAll();
            }
            else
            {
                ApplyAll(aircraft, context);
            }
        }

        /// <summary>
        /// Called after every rebuild of a procedural part mesh (designer edits). A rebuild rewrites UV0
        /// and UV1 and may replace the renderer map, so the texture is applied again.
        /// Parts created while the texture is active are picked up here too.
        /// </summary>
        public static void OnMeshRebuilt(ProceduralPartMeshRenderer procedural)
        {
            if (Parts.Count == 0 || _activeAircraft == null)
            {
                return;
            }

            MeshRenderer meshRenderer = procedural.MeshRenderer;
            if (meshRenderer == null)
            {
                return;
            }

            PartScript part = meshRenderer.GetComponentInParent<PartScript>();
            if (part == null || part.Aircraft != _activeAircraft || part.PartMaterialScript == null)
            {
                return;
            }

            PartState state;
            if (!Parts.TryGetValue(part, out state))
            {
                state = new PartState { Part = part, Materials = part.PartMaterialScript, Aircraft = part.Aircraft };
                Parts[part] = state;
                state.Materials.OnPaintedInDesigner += OnPainted;
            }

            ThemeScript theme = state.Aircraft.Theme;
            foreach (PartMaterialScript.RendererMaterialMap map in state.Materials.RendererMaps)
            {
                string reason;
                if (map.Renderer == meshRenderer && IsEligible(theme, map, out reason))
                {
                    ApplyRenderer(state.Aircraft, theme, state, map);
                }
            }

            state.Materials.RefreshHighlight();
        }

        // ------------------------------------------------------------------ apply

        private static void ApplyAll(AircraftScript aircraft, string context)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            ThemeScript theme = aircraft.Theme;
            if (theme == null || theme.Theme == null || theme.Material == null)
            {
                Plugin.Log.LogWarning(Tag + "aircraft has no usable theme");
                return;
            }

            if (aircraft.LoadContext != CraftLoadContext.Designer)
            {
                Plugin.Log.LogWarning(Tag + "context is " + context + ", not the designer: in flight the game merges most part meshes at load, "
                    + "so only parts that are never merged (wings, flaps) are expected to show the texture. Results here are not representative.");
            }

            if (_texture == null)
            {
                _texture = TestTexture.Create();
            }

            _activeAircraft = aircraft;

            int parts = 0;
            int renderers = 0;
            int skipped = 0;
            Dictionary<string, int> reasons = new Dictionary<string, int>();

            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || part.PartMaterialScript == null)
                {
                    continue;
                }

                PartState state = new PartState { Part = part, Materials = part.PartMaterialScript, Aircraft = aircraft };
                int applied = 0;

                foreach (PartMaterialScript.RendererMaterialMap map in state.Materials.RendererMaps)
                {
                    string reason;
                    if (!IsEligible(theme, map, out reason))
                    {
                        skipped++;
                        int n;
                        reasons.TryGetValue(reason, out n);
                        reasons[reason] = n + 1;
                        continue;
                    }

                    if (ApplyRenderer(aircraft, theme, state, map))
                    {
                        applied++;
                    }
                }

                if (applied > 0)
                {
                    Parts[part] = state;
                    state.Materials.OnPaintedInDesigner += OnPainted;
                    state.Materials.RefreshHighlight();
                    parts++;
                    renderers += applied;
                }
            }

            System.Text.StringBuilder skippedText = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in reasons)
            {
                if (skippedText.Length > 0)
                {
                    skippedText.Append("; ");
                }

                skippedText.Append(pair.Key).Append(" x").Append(pair.Value);
            }

            int albedoIndex = -1;
            try { albedoIndex = AlbedoShaderIndex(theme); } catch (Exception) { }

            Plugin.Log.LogInfo(Tag + "applied to " + renderers + " renderer(s) of " + parts + " part(s) in " + watch.ElapsedMilliseconds
                + " ms | style=" + Style + " shaderIndex=" + albedoIndex + " tilesPerMeter=" + Plugin.TestTilesPerMeter.Value
                + " | skipped " + skipped + (skippedText.Length > 0 ? " (" + skippedText + ")" : ""));
            Plugin.Log.LogInfo(Tag + "press the key again to revert. The craft file is not modified.");
        }

        private static bool IsEligible(ThemeScript theme, PartMaterialScript.RendererMaterialMap map, out string reason)
        {
            reason = null;

            if (map.Renderer == null)
            {
                reason = "renderer destroyed";
                return false;
            }

            if (!map.MeshIsUnique)
            {
                reason = "shared mesh (premade part)";
                return false;
            }

            Mesh mesh = map.Mesh;
            if (mesh == null || mesh.vertexCount == 0)
            {
                reason = "empty mesh";
                return false;
            }

            if (!mesh.isReadable)
            {
                reason = "mesh not readable";
                return false;
            }

            Material[] originals = map.OriginalMaterials;
            if (originals == null || originals.Length == 0 || originals[0] == null)
            {
                reason = "no materials";
                return false;
            }

            if (originals[0].shader != theme.Material.shader)
            {
                reason = "other shader '" + originals[0].shader.name + "'";
                return false;
            }

            PartRendererMaterialType[] types = map.MaterialTypes;
            if (types != null)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != PartRendererMaterialType.DefaultShared && types[i] != PartRendererMaterialType.DefaultInstanced)
                    {
                        reason = "custom material type " + types[i];
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ApplyRenderer(AircraftScript aircraft, ThemeScript theme, PartState state, PartMaterialScript.RendererMaterialMap map)
        {
            try
            {
                Material atlas = GetAtlasMaterial(theme);
                if (atlas == null)
                {
                    return false;
                }

                Mesh mesh = map.Mesh;
                UvProjection.BoxProject(mesh, map.Renderer.transform, aircraft.transform, Plugin.TestTilesPerMeter.Value);
                if (!UvProjection.SetMaterialIndex(mesh, AlbedoShaderIndex(theme)))
                {
                    Plugin.Log.LogWarning(Tag + "mesh '" + mesh.name + "' has no usable UV1");
                    return false;
                }

                // Remember the originals only the first time this exact map is touched.
                RendererState rs;
                if (!state.Renderers.TryGetValue(map.Renderer, out rs) || rs.Map != map)
                {
                    rs = new RendererState
                    {
                        Map = map,
                        OriginalMaterials = map.OriginalMaterials,
                        OriginalHighlight = map.HighlightMaterials,
                        OriginalSelected = map.SelectedMaterials,
                        OriginalExcludeFromCombine = map.ExcludeFromMeshCombine
                    };
                    state.Renderers[map.Renderer] = rs;
                }

                int count = map.OriginalMaterials.Length;
                map.OriginalMaterials = Fill(count, atlas);

                // The highlight copies (designer only) must carry the texture too, or hovering a part would hide it.
                if (rs.OriginalHighlight != null)
                {
                    map.HighlightMaterials = Fill(count, theme.InitializeHighlightMaterial(atlas));
                }

                if (rs.OriginalSelected != null)
                {
                    map.SelectedMaterials = Fill(count, theme.InitializeHighlightSelectedMaterial(atlas));
                }

                // In flight the game merges meshes that share a material; a private material must not be merged.
                map.ExcludeFromMeshCombine = true;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not texture a renderer of part '" + state.Part.Part.Name + "': " + ex);
                return false;
            }
        }

        private static Material GetAtlasMaterial(ThemeScript theme)
        {
            Material material;
            if (AtlasMaterials.TryGetValue(theme, out material) && material != null)
            {
                return material;
            }

            // A clone of the shared part material that the theme keeps up to date (_MaterialData...).
            material = theme.RequestDefaultPartMaterialInstance();
            if (material == null)
            {
                Plugin.Log.LogWarning(Tag + "the theme did not give a material instance");
                return null;
            }

            material.name = "SP2TextureMod Atlas";
            material.SetTexture(BaseMapId, _texture);
            if (Plugin.SupersampledStyle.Value)
            {
                material.SetFloat(SupersampleBiasId, -1f);
            }

            AtlasMaterials[theme] = material;
            return material;
        }

        private static int AlbedoShaderIndex(ThemeScript theme)
        {
            PartMaterial reserved = theme.Theme.GetReservedMaterial(Style);
            return theme.Theme.GetMaterialIndex(reserved.Id);
        }

        private static Material[] Fill(int count, Material material)
        {
            Material[] array = new Material[count];
            for (int i = 0; i < count; i++)
            {
                array[i] = material;
            }

            return array;
        }

        // Fired by the game after a part is painted or its theme changes: the paint step rewrites UV1.x.
        private static void OnPainted(object sender, PartMaterialScript.PaintedEventArgs e)
        {
            try
            {
                PartMaterialScript materials = sender as PartMaterialScript;
                if (materials == null)
                {
                    return;
                }

                PartScript part = materials.GetComponent<PartScript>();
                PartState state;
                if (part == null || !Parts.TryGetValue(part, out state))
                {
                    return;
                }

                ThemeScript theme = state.Aircraft.Theme;
                foreach (PartMaterialScript.RendererMaterialMap map in state.Materials.RendererMaps)
                {
                    string reason;
                    if (IsEligible(theme, map, out reason))
                    {
                        ApplyRenderer(state.Aircraft, theme, state, map);
                    }
                }

                state.Materials.RefreshHighlight();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "re-applying after paint failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ revert

        private static void RevertAll()
        {
            int renderers = 0;

            foreach (PartState state in new List<PartState>(Parts.Values))
            {
                if (state.Materials == null)
                {
                    continue;
                }

                state.Materials.OnPaintedInDesigner -= OnPainted;

                foreach (RendererState rs in state.Renderers.Values)
                {
                    try
                    {
                        if (rs.Map.Renderer != null)
                        {
                            rs.Map.OriginalMaterials = rs.OriginalMaterials;
                            rs.Map.HighlightMaterials = rs.OriginalHighlight;
                            rs.Map.SelectedMaterials = rs.OriginalSelected;
                            rs.Map.ExcludeFromMeshCombine = rs.OriginalExcludeFromCombine;

                            Mesh mesh = rs.Map.Mesh;
                            if (mesh != null && mesh.vertexCount > 0)
                            {
                                UvProjection.ClearUv0(mesh);
                            }

                            renderers++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning(Tag + "revert of a renderer failed: " + ex.Message);
                    }
                }

                try
                {
                    // Rewrites UV1.x from the part's real material ids (never changed by this mod).
                    state.Materials.ApplyAllMaterials();
                    state.Materials.RefreshHighlight();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "restoring the paint of a part failed: " + ex.Message);
                }
            }

            Parts.Clear();

            // The renderers no longer use the clone, so it can be released (this also frees its highlight copies).
            foreach (KeyValuePair<ThemeScript, Material> pair in AtlasMaterials)
            {
                if (pair.Value != null)
                {
                    pair.Key.ReleaseDefaultPartMaterialInstance(pair.Value);
                }
            }

            AtlasMaterials.Clear();

            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }

            _activeAircraft = null;
            Plugin.Log.LogInfo(Tag + "reverted " + renderers + " renderer(s)");
        }

        // Forget parts and materials that were destroyed with their aircraft (left the designer, loaded another craft).
        private static void Prune()
        {
            List<PartScript> dead = new List<PartScript>();
            foreach (KeyValuePair<PartScript, PartState> pair in Parts)
            {
                if (pair.Key == null || pair.Value.Materials == null)
                {
                    dead.Add(pair.Key);
                }
            }

            for (int i = 0; i < dead.Count; i++)
            {
                Parts.Remove(dead[i]);
            }

            List<ThemeScript> deadThemes = new List<ThemeScript>();
            foreach (KeyValuePair<ThemeScript, Material> pair in AtlasMaterials)
            {
                if (pair.Value == null)
                {
                    deadThemes.Add(pair.Key);
                }
            }

            for (int i = 0; i < deadThemes.Count; i++)
            {
                AtlasMaterials.Remove(deadThemes[i]);
            }

            if (Parts.Count == 0)
            {
                _activeAircraft = null;
            }
        }
    }
}
