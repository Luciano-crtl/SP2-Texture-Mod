using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Paint;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.MeshGen;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Puts the user's texture on the procedural parts (fuselage, wings...) of the current aircraft,
    /// reversibly:
    ///   1. write UV0 (procedural meshes have none): either by transferring the UVs of a War Thunder OBJ
    ///      model aligned to the craft, or, with an image alone, by a box projection (tiling);
    ///   2. point UV1.x at the game's reserved albedo paint style (the one used by gauge faces);
    ///   3. give the renderers a clone of the part material whose _BaseMap is the image.
    /// Nothing is saved in the craft file: MaterialIds are never touched. The same key reverts everything.
    /// </summary>
    internal static class AtlasApplier
    {
        private const string Tag = "[TEX] ";

        // The textured style the game itself uses (gauge faces). The plain AlbedoTexture style is not drawn by the shader.
        private const PaintStyle Style = PaintStyle.AlbedoTextureSupersampledWithMipmapBias;

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

        private sealed class Candidate
        {
            public PartScript Part;
            public PartMaterialScript Materials;
            public PartMaterialScript.RendererMaterialMap Map;
        }

        private static readonly Dictionary<PartScript, PartState> Parts = new Dictionary<PartScript, PartState>();
        private static readonly Dictionary<ThemeScript, Material> AtlasMaterials = new Dictionary<ThemeScript, Material>();

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int SupersampleBiasId = Shader.PropertyToID("_SupersampleMipmapBias");

        // State of the current application.
        private static Texture2D _texture;
        private static AircraftScript _activeAircraft;
        private static ObjModel _model;
        private static TriangleBvh _bvh;
        private static ModelAlignment _alignment;
        private static readonly UvTransfer.Stats Stats = new UvTransfer.Stats();
        private static int _verifyFrame = -1;

        // The automatic alignment is kept while the aircraft, the model and the mirror setting stay the same,
        // so moving an adjustment slider is fast.
        private static ModelAlignment _fitCache;
        private static AircraftScript _fitAircraft;
        private static string _fitModelPath;
        private static bool _fitFlip;

        // The parsed OBJ is kept between applications (parsing a large model takes a moment).
        private static string _cachePath;
        private static DateTime _cacheStamp;
        private static bool _cacheFlip;
        private static ObjModel _cacheModel;
        private static TriangleBvh _cacheBvh;

        /// <summary>One-line result shown on screen and in the log.</summary>
        public static string StatusText = "";

        public static bool IsActive
        {
            get { return Parts.Count > 0; }
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Called every frame by the plugin; runs the delayed self-check after applying.</summary>
        public static void Tick()
        {
            if (_verifyFrame < 0 || Time.frameCount < _verifyFrame)
            {
                return;
            }

            _verifyFrame = -1;
            try
            {
                Verify();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "verify failed: " + ex);
            }
        }

        /// <summary>Applies the selected image (and model) to the aircraft open in the designer; applies again if already applied.</summary>
        public static void ApplyCurrent()
        {
            string context;
            AircraftScript aircraft = DiagnosticDump.FindAircraft(out context);
            if (aircraft == null)
            {
                Fail("No aircraft found. Open a craft in the designer first.");
                return;
            }

            Prune();
            if (Parts.Count > 0)
            {
                ReapplyIfActive();
            }
            else
            {
                ApplyAll(aircraft, context);
            }
        }

        /// <summary>Removes the texture from the aircraft and restores its normal paint.</summary>
        public static void RemoveCurrent()
        {
            Prune();
            if (Parts.Count > 0)
            {
                RevertAll();
            }
            else
            {
                StatusText = "Nothing to remove.";
            }
        }

        /// <summary>Forgets the automatic alignment so the next apply finds it again (after the craft changed a lot).</summary>
        public static void InvalidateFit()
        {
            _fitCache = null;
        }

        /// <summary>After changing the image or the tiling: applies again with the new choice, if currently applied.</summary>
        public static void ReapplyIfActive()
        {
            Prune();
            if (Parts.Count == 0 || _activeAircraft == null)
            {
                return;
            }

            AircraftScript aircraft = _activeAircraft;
            string context = aircraft.LoadContext.ToString();
            RevertAll();
            ApplyAll(aircraft, context);
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
                Fail("The aircraft has no usable theme.");
                return;
            }

            if (aircraft.LoadContext != CraftLoadContext.Designer)
            {
                Plugin.Log.LogWarning(Tag + "context is " + context + ", not the designer: in flight the game merges most part meshes at load, "
                    + "so only parts that are never merged (wings, flaps) are expected to show the texture. Results here are not representative.");
            }

            TextureLibrary.Entry entry = TextureLibrary.CurrentImage;
            if (!LoadImage(entry))
            {
                return;
            }

            bool transfer = false;
            string modelPath = TextureLibrary.CurrentModelPath;
            if (modelPath != null)
            {
                transfer = PrepareModel(modelPath);
            }

            _texture.wrapMode = transfer ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            if (!transfer)
            {
                _model = null;
                _bvh = null;
                _alignment = null;
            }

            // 1. Which renderers can be textured.
            List<Candidate> candidates = new List<Candidate>();
            Dictionary<string, int> reasons = new Dictionary<string, int>();
            int skipped = 0;
            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || part.PartMaterialScript == null)
                {
                    continue;
                }

                foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                {
                    string reason;
                    if (IsEligible(theme, map, out reason))
                    {
                        candidates.Add(new Candidate { Part = part, Materials = part.PartMaterialScript, Map = map });
                    }
                    else
                    {
                        skipped++;
                        int n;
                        reasons.TryGetValue(reason, out n);
                        reasons[reason] = n + 1;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                ReleaseTexture();
                Fail("No procedural part (fuselage, wing...) was found on this aircraft.");
                return;
            }

            _activeAircraft = aircraft;

            // 2. Align the model to the craft.
            string fit = "";
            if (transfer)
            {
                transfer = FitModel(aircraft, candidates, out fit);
                if (!transfer)
                {
                    _texture.wrapMode = TextureWrapMode.Repeat;
                }
            }

            // 3. Write UVs, styles and materials.
            Stats.Reset();
            int parts = 0;
            int renderers = 0;
            foreach (Candidate candidate in candidates)
            {
                PartState state;
                if (!Parts.TryGetValue(candidate.Part, out state))
                {
                    state = new PartState { Part = candidate.Part, Materials = candidate.Materials, Aircraft = aircraft };
                    Parts[candidate.Part] = state;
                    state.Materials.OnPaintedInDesigner += OnPainted;
                }

                if (ApplyRenderer(aircraft, theme, state, candidate.Map))
                {
                    renderers++;
                }
            }

            foreach (PartState state in new List<PartState>(Parts.Values))
            {
                if (state.Renderers.Count == 0)
                {
                    state.Materials.OnPaintedInDesigner -= OnPainted;
                    Parts.Remove(state.Part);
                }
                else
                {
                    parts++;
                    state.Materials.RefreshHighlight();
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

            string mode = transfer
                ? "model UV transfer (" + _model.Name + ")"
                : "tiled, " + Plugin.TilesPerMeter.Value.ToString("0.##", CultureInfo.InvariantCulture) + " per meter";

            Plugin.Log.LogInfo(Tag + "applied '" + _textureName() + "' to " + renderers + " renderer(s) of " + parts + " part(s) in "
                + watch.ElapsedMilliseconds + " ms | mode: " + mode
                + " | skipped " + skipped + (skippedText.Length > 0 ? " (" + skippedText + ")" : ""));
            if (transfer)
            {
                Plugin.Log.LogInfo(Tag + "match quality: " + Stats.Describe(Plugin.MaxMatchDistance.Value));
            }

            StatusText = "Applied to " + renderers + " renderers, mode: " + mode
                + (transfer ? "\n" + fit : "")
                + (transfer ? "\n" + Stats.Describe(Plugin.MaxMatchDistance.Value) : "")
                + "\nPress the apply key again to remove. The craft file is not modified.";
            _verifyFrame = Time.frameCount + 5;
        }

        private static string _textureName()
        {
            return _texture != null ? _texture.name : "?";
        }

        private static bool LoadImage(TextureLibrary.Entry entry)
        {
            if (entry == null)
            {
                _texture = TestTexture.Create();
                return true;
            }

            Texture2D texture = TextureLibrary.LoadTexture(entry);
            if (texture == null)
            {
                Fail("Could not read the image '" + entry.FileName + "' (PNG or JPG expected).");
                return false;
            }

            _texture = texture;
            return true;
        }

        // Reads and indexes the OBJ (cached while the file and the mirror setting do not change).
        private static bool PrepareModel(string path)
        {
            try
            {
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(path);
                bool flip = Plugin.FlipModelX.Value;
                if (_cacheModel == null || _cachePath != path || _cacheStamp != stamp || _cacheFlip != flip)
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    string error;
                    ObjModel model = ObjModel.Load(path, flip, out error);
                    if (model == null)
                    {
                        Plugin.Log.LogWarning(Tag + "model not used: " + error);
                        StatusText = "Model not used: " + error;
                        return false;
                    }

                    _cacheBvh = new TriangleBvh(model);
                    _cacheModel = model;
                    _cachePath = path;
                    _cacheStamp = stamp;
                    _cacheFlip = flip;

                    Bounds b = model.Bounds;
                    Plugin.Log.LogInfo(Tag + "model '" + model.Name + "': " + model.TriangleCount + " triangles, size ("
                        + F(b.size.x) + ", " + F(b.size.y) + ", " + F(b.size.z) + "), mirrored on X=" + flip
                        + ", loaded and indexed in " + watch.ElapsedMilliseconds + " ms");
                }

                _model = _cacheModel;
                _bvh = _cacheBvh;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "model not used: " + ex.Message);
                StatusText = "Model not used: " + ex.Message;
                _model = null;
                _bvh = null;
                return false;
            }
        }

        // Samples the craft's procedural surface and finds the transform that puts the model on it.
        private static bool FitModel(AircraftScript aircraft, List<Candidate> candidates, out string summary)
        {
            summary = "";
            try
            {
                int total = 0;
                foreach (Candidate c in candidates)
                {
                    total += c.Map.Mesh.vertexCount;
                }

                int stride = Mathf.Max(1, total / 700);
                List<Vector3> samples = new List<Vector3>(800);
                List<Vector3> vertices = new List<Vector3>();
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                int counter = 0;
                foreach (Candidate c in candidates)
                {
                    vertices.Clear();
                    c.Map.Mesh.GetVertices(vertices);
                    Matrix4x4 toCraft = aircraft.transform.worldToLocalMatrix * c.Map.Renderer.transform.localToWorldMatrix;
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 p = toCraft.MultiplyPoint3x4(vertices[i]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        if (counter++ % stride == 0)
                        {
                            samples.Add(p);
                        }
                    }
                }

                Bounds bounds = new Bounds();
                bounds.SetMinMax(min, max);

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                bool reused = _fitCache != null && _fitAircraft == aircraft && _fitModelPath == _cachePath && _fitFlip == _cacheFlip;
                ModelAlignment auto = reused ? _fitCache : ModelAlignment.Fit(_model, _bvh, samples, bounds);
                _fitCache = auto;
                _fitAircraft = aircraft;
                _fitModelPath = _cachePath;
                _fitFlip = _cacheFlip;
                _alignment = auto.WithAdjustment(
                    bounds.center,
                    new Vector3(Plugin.AdjustOffsetX.Value, Plugin.AdjustOffsetY.Value, Plugin.AdjustOffsetZ.Value),
                    new Vector3(Plugin.AdjustRotationX.Value, Plugin.AdjustRotationY.Value, Plugin.AdjustRotationZ.Value),
                    Plugin.AdjustScale.Value);

                summary = (reused ? "Alignment (kept): " : "Alignment: ") + auto.Summary;
                Plugin.Log.LogInfo(Tag + "craft size (" + F(bounds.size.x) + ", " + F(bounds.size.y) + ", " + F(bounds.size.z)
                    + ") | alignment " + (reused ? "kept from before" : "found in " + watch.ElapsedMilliseconds + " ms") + ": " + auto.Summary);
                if (auto.MeanDistance > 0.08f * Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)))
                {
                    Plugin.Log.LogWarning(Tag + "the model does not fit the craft well (mean distance is large). "
                        + "Is it the same aircraft? Use the Adjust* settings or try the setting FlipModelX = false.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not align the model, tiling instead: " + ex);
                StatusText = "Could not align the model, tiling instead: " + ex.Message;
                _alignment = null;
                return false;
            }
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
                if (_alignment != null && _model != null && _bvh != null)
                {
                    UvTransfer.Write(mesh, map.Renderer.transform, aircraft.transform, _model, _bvh, _alignment,
                        Plugin.NormalWeight.Value, Plugin.MaxMatchDistance.Value, Stats);
                }
                else
                {
                    UvProjection.BoxProject(mesh, map.Renderer.transform, aircraft.transform, Plugin.TilesPerMeter.Value);
                }

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
            material.SetFloat(SupersampleBiasId, -1f);

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
            ReleaseTexture();

            _model = null;
            _bvh = null;
            _alignment = null;
            _activeAircraft = null;
            _verifyFrame = -1;
            StatusText = "Removed (" + renderers + " renderers restored).";
            Plugin.Log.LogInfo(Tag + "reverted " + renderers + " renderer(s)");
        }

        private static void ReleaseTexture()
        {
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
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

        private static void Fail(string message)
        {
            StatusText = message;
            Plugin.Log.LogWarning(Tag + message);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ self-check

        // Runs a few frames after applying and writes what the renderers really use now.
        private static void Verify()
        {
            if (_activeAircraft == null || Parts.Count == 0)
            {
                return;
            }

            ThemeScript theme = _activeAircraft.Theme;
            Material atlas;
            if (theme == null || !AtlasMaterials.TryGetValue(theme, out atlas) || atlas == null)
            {
                Plugin.Log.LogWarning(Tag + "verify: the atlas material no longer exists");
                return;
            }

            Texture bound = atlas.GetTexture(BaseMapId);
            Plugin.Log.LogInfo(Tag + "verify: material '" + atlas.name + "' shader='" + atlas.shader.name + "' baseMap="
                + (bound != null ? bound.name + " " + bound.width + "x" + bound.height : "NULL"));

            int shown = 0;
            foreach (PartState state in Parts.Values)
            {
                foreach (RendererState rs in state.Renderers.Values)
                {
                    if (shown >= 2)
                    {
                        return;
                    }

                    Renderer renderer = rs.Map.Renderer;
                    if (renderer == null)
                    {
                        continue;
                    }

                    shown++;
                    Material[] now = renderer.sharedMaterials;
                    int usingAtlas = 0;
                    for (int i = 0; i < now.Length; i++)
                    {
                        if (now[i] == atlas)
                        {
                            usingAtlas++;
                        }
                    }

                    Plugin.Log.LogInfo(Tag + "verify: part '" + state.Part.Part.Name + "' #" + state.Part.Part.Id
                        + " submeshes using the atlas material=" + usingAtlas + "/" + now.Length
                        + " | " + DiagnosticDump.MeshSummary(rs.Map.Mesh));
                }
            }
        }
    }
}
