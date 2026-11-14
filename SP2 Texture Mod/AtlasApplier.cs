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
            public bool WroteTangents;
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
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");
        private static readonly int ParallaxMapId = Shader.PropertyToID("_ParallaxMap");
        private static readonly int ParallaxId = Shader.PropertyToID("_Parallax");
        private static readonly int MaterialDataId = Shader.PropertyToID("_MaterialData");

        // State of the current application.
        private static Texture2D _texture;
        private static Texture2D _normalMap;
        private static Texture2D _occlusionMap;
        private static Texture2D _heightMap;
        private static AircraftScript _activeAircraft;
        private static ObjModel _model;
        private static TriangleBvh _bvh;
        private static ModelAlignment _alignment;
        private static readonly UvTransfer.Stats Stats = new UvTransfer.Stats();
        private static int _verifyFrame = -1;

        // The automatic alignment is kept while the aircraft, the model and the mirror setting stay the same,
        // so moving an adjustment slider is fast.
        private static List<ModelAlignment> _fitCandidates;
        private static int _fitChoice;
        private static AircraftScript _fitAircraft;
        private static string _fitModelPath;
        private static bool _fitFlip;

        // How the UVs are looked up on the model (set while fitting, used for every renderer, also after edits).
        private static UvTransfer.Options _options;
        private static bool _frontFaces;

        // The parsed OBJ is kept between applications (parsing a large model takes a moment).
        private static string _cachePath;
        private static DateTime _cacheStamp;
        private static bool _cacheFlip;
        private static ObjModel _cacheModel;
        private static TriangleBvh _cacheBvh;

        /// <summary>Short result of the last action, for the panel.</summary>
        public static string StatusText = "";

        /// <summary>Details of the last apply as (label, short value) pairs, for the panel.</summary>
        public static readonly List<string[]> StatusRows = new List<string[]>();

        private static string _facingShort = "";

        private static void SetStatus(string text)
        {
            StatusText = text;
            StatusRows.Clear();
        }

        private static void StatusAdd(string label, string value)
        {
            StatusRows.Add(new[] { label, value });
        }

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
                SetStatus("Nothing to remove.");
            }
        }

        /// <summary>Forgets the automatic alignment so the next apply finds it again (after the craft changed a lot).</summary>
        public static void InvalidateFit()
        {
            _fitCandidates = null;
            _fitChoice = 0;
        }

        /// <summary>Tries the next best orientation of the model (when the best one has the model upside-down, for instance).</summary>
        public static void NextOrientation()
        {
            if (_fitCandidates == null || _fitCandidates.Count < 2)
            {
                SetStatus("No other orientation to try. Apply with a model first.");
                return;
            }

            _fitChoice = (_fitChoice + 1) % _fitCandidates.Count;
            ReapplyIfActive();
        }

        /// <summary>"2/6" and the quality of the orientation in use, for the panel.</summary>
        public static string OrientationText
        {
            get
            {
                if (_fitCandidates == null || _fitCandidates.Count == 0)
                {
                    return "-";
                }

                ModelAlignment current = _fitCandidates[Mathf.Clamp(_fitChoice, 0, _fitCandidates.Count - 1)];
                return (Mathf.Clamp(_fitChoice, 0, _fitCandidates.Count - 1) + 1) + "/" + _fitCandidates.Count
                    + " (mean distance " + F(current.MeanDistance) + " m)";
            }
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

            LoadMaps();

            bool transfer = false;
            string modelPath = TextureLibrary.CurrentModelPath;
            if (modelPath != null)
            {
                transfer = PrepareModel(modelPath);
            }

            // Always repeat: tiling needs it, and the model's texture coordinates may lie a little outside [0, 1].
            // (With "clamp" a negative v, which many exports use, sampled one single row of the image.)
            _texture.wrapMode = TextureWrapMode.Repeat;
            if (!transfer)
            {
                _model = null;
                _bvh = null;
                _alignment = null;
                _options = null;
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
            string debugNote = "";
            if (transfer)
            {
                Plugin.Log.LogInfo(Tag + "match quality: " + Stats.Describe(Plugin.MaxMatchDistance.Value));
                try
                {
                    string mapPath = UvDebugMap.Write(_texture, Stats.Points);
                    Plugin.Log.LogInfo(Tag + "UV debug map written to " + mapPath
                        + " (red = vertices facing up, blue = facing down, green = sideways, yellow = nearest-surface fallback)");
                    debugNote = " Debug map: " + UvDebugMap.FileName;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "could not write the UV debug map: " + ex.Message);
                }
            }

            StatusText = "Applied to " + renderers + " renderers in " + watch.ElapsedMilliseconds + " ms";
            StatusRows.Clear();
            StatusAdd("Mode", transfer ? "model UV transfer" : "tiled, " + F(Plugin.TilesPerMeter.Value) + " per meter");
            StatusAdd("Maps", MapsSummary());
            if (transfer)
            {
                StatusAdd("Model", _model.Name);
                StatusAdd("Orientation", OrientationText);
                StatusAdd("Model faces", _facingShort);
                StatusAdd("Match", Stats.MatchSummary());
                StatusAdd("Faces up", Stats.KindSummary(0));
                StatusAdd("Faces down", Stats.KindSummary(1));
                StatusAdd("Sideways", Stats.KindSummary(2));
                StatusAdd("Texture coords", Stats.UvRangeSummary());
                if (debugNote.Length > 0)
                {
                    StatusAdd("Debug map", UvDebugMap.FileName);
                }
            }

            StatusAdd("Craft file", "not modified");
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
                        SetStatus("Model not used: " + error);
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
                SetStatus("Model not used: " + ex.Message);
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

                int stride = Mathf.Max(1, total / 400);
                List<Vector3> samples = new List<Vector3>(800);
                List<Vector3> sampleNormals = new List<Vector3>(800);
                List<Vector3> vertices = new List<Vector3>();
                List<Vector3> normals = new List<Vector3>();
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                int counter = 0;
                foreach (Candidate c in candidates)
                {
                    vertices.Clear();
                    normals.Clear();
                    c.Map.Mesh.GetVertices(vertices);
                    c.Map.Mesh.GetNormals(normals);
                    bool hasNormals = normals.Count == vertices.Count;
                    Matrix4x4 toCraft = aircraft.transform.worldToLocalMatrix * c.Map.Renderer.transform.localToWorldMatrix;
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 p = toCraft.MultiplyPoint3x4(vertices[i]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        if (counter++ % stride == 0)
                        {
                            samples.Add(p);
                            sampleNormals.Add(hasNormals ? toCraft.MultiplyVector(normals[i]).normalized : Vector3.zero);
                        }
                    }
                }

                Bounds bounds = new Bounds();
                bounds.SetMinMax(min, max);

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                bool reused = _fitCandidates != null && _fitCandidates.Count > 0 && _fitAircraft == aircraft
                    && _fitModelPath == _cachePath && _fitFlip == _cacheFlip;
                if (!reused)
                {
                    _fitCandidates = ModelAlignment.FitCandidates(_model, _bvh, samples, bounds);
                    _fitChoice = 0;
                    _fitAircraft = aircraft;
                    _fitModelPath = _cachePath;
                    _fitFlip = _cacheFlip;

                    System.Text.StringBuilder list = new System.Text.StringBuilder();
                    for (int i = 0; i < _fitCandidates.Count; i++)
                    {
                        if (i > 0)
                        {
                            list.Append(" ; ");
                        }

                        list.Append(i + 1).Append(": ").Append(_fitCandidates[i].Summary);
                    }

                    Plugin.Log.LogInfo(Tag + "orientation candidates, best first: " + list);
                }

                _fitChoice = Mathf.Clamp(_fitChoice, 0, _fitCandidates.Count - 1);
                ModelAlignment auto = _fitCandidates[_fitChoice];
                _alignment = auto.WithAdjustment(
                    bounds.center,
                    new Vector3(Plugin.AdjustOffsetX.Value, Plugin.AdjustOffsetY.Value, Plugin.AdjustOffsetZ.Value),
                    new Vector3(Plugin.AdjustRotationX.Value, Plugin.AdjustRotationY.Value, Plugin.AdjustRotationZ.Value),
                    Plugin.AdjustScale.Value);

                // Do the model's faces wind the way its normals need? Compare, at the matching points, the model's
                // face normal with the craft's own (good) normals; if mostly opposite, the model is inside-out.
                float agreement = FacingAgreement(samples, sampleNormals, _alignment);
                string facing;
                if (agreement < 0.5f)
                {
                    _bvh.SetNormalsInverted(!_bvh.NormalsInverted);
                    float after = FacingAgreement(samples, sampleNormals, _alignment);
                    facing = "model faces looked inside-out (" + Percent(agreement) + " agreement), turned around: now " + Percent(after);
                    _facingShort = "turned around, " + Percent(agreement) + " to " + Percent(after);
                    agreement = after;
                }
                else
                {
                    facing = "model faces agree with the craft's normals at " + Percent(agreement);
                    _facingShort = "agree " + Percent(agreement);
                }

                // With consistent faces only the faces that look at the ray count; otherwise the first surface hit does.
                _frontFaces = agreement >= 0.75f;

                _options = new UvTransfer.Options
                {
                    Project = Plugin.ProjectAlongNormals.Value,
                    RayStartMeters = Plugin.RayStart.Value,
                    MaxDistanceMeters = Plugin.MaxMatchDistance.Value,
                    FrontFacesOnly = _frontFaces,
                    FacingWeightSquareMeters = Plugin.FacingWeight.Value,
                    FarLimitMeters = Plugin.MaxMatchDistance.Value
                };

                summary = "Orientation " + OrientationText + ": " + auto.Summary + ". " + facing;
                Plugin.Log.LogInfo(Tag + "craft size (" + F(bounds.size.x) + ", " + F(bounds.size.y) + ", " + F(bounds.size.z)
                    + ") | alignment " + (reused ? "kept from before" : "found in " + watch.ElapsedMilliseconds + " ms")
                    + " | using " + summary + " | method: " + (_options.Project ? "project along normals" : "nearest surface")
                    + ", front faces only: " + _frontFaces);
                if (auto.MeanDistance > 0.08f * Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)))
                {
                    Plugin.Log.LogWarning(Tag + "the model does not fit the craft well (mean distance is large). "
                        + "Is it the same aircraft? Try the next orientation, the Adjust settings or FlipModelX = false.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not align the model, tiling instead: " + ex);
                SetStatus("Could not align the model, tiling instead: " + ex.Message);
                _alignment = null;
                _options = null;
                return false;
            }
        }

        // Share of the samples whose nearest model face points the same way as the craft's normal there.
        private static float FacingAgreement(List<Vector3> positions, List<Vector3> normals, ModelAlignment alignment)
        {
            int agree = 0;
            int total = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                if (normals[i].sqrMagnitude < 0.5f)
                {
                    continue;
                }

                Vector3 pObj = alignment.CraftToObj.MultiplyPoint3x4(positions[i]);
                int triangle;
                float u, v, w, distanceSquared;
                if (!_bvh.Nearest(pObj, Vector3.zero, 0f, out triangle, out u, out v, out w, out distanceSquared))
                {
                    continue;
                }

                Vector3 faceNormal = alignment.ObjToCraft.MultiplyVector(_bvh.NormalOf(triangle)).normalized;
                total++;
                if (Vector3.Dot(faceNormal, normals[i]) > 0f)
                {
                    agree++;
                }
            }

            return total == 0 ? 1f : (float)agree / total;
        }

        private static string Percent(float fraction)
        {
            return (100f * fraction).ToString("0", CultureInfo.InvariantCulture) + "%";
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
                if (_alignment != null && _model != null && _bvh != null && _options != null)
                {
                    UvTransfer.Write(mesh, map.Renderer.transform, aircraft.transform, _model, _bvh, _alignment, _options, Stats);
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

                // A normal map (and the parallax effect) needs the tangent of every vertex, which these meshes do not
                // have: they are worked out from the normals and the texture coordinates just written.
                bool tangents = false;
                if (_normalMap != null || _heightMap != null)
                {
                    mesh.RecalculateTangents();
                    tangents = true;
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

                if (tangents)
                {
                    rs.WroteTangents = true;
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
            ConfigureMaterial(material, theme);

            AtlasMaterials[theme] = material;
            return material;
        }

        // ------------------------------------------------------------------ maps and material values

        // Loads the normal, occlusion and height maps chosen in the panel (any of them may be missing).
        private static void LoadMaps()
        {
            ReleaseMaps();

            TextureLibrary.Entry normal = TextureLibrary.Normal.Current;
            TextureLibrary.Entry occlusion = TextureLibrary.Occlusion.Current;
            TextureLibrary.Entry height = TextureLibrary.Height.Current;
            string note;

            try
            {
                if (normal != null)
                {
                    _normalMap = MapTextures.LoadNormal(normal.FilePath, Plugin.FlipNormalGreen.Value, Plugin.RebuildNormalZ.Value,
                        Plugin.BumpStrength.Value, out note);
                    LogMap("normal map", normal.FileName, _normalMap, note);
                }
                else if (Plugin.HeightToNormal.Value && height != null)
                {
                    _normalMap = MapTextures.LoadNormalFromHeight(height.FilePath, Plugin.BumpStrength.Value, out note);
                    LogMap("normal map from the height map", height.FileName, _normalMap, note);
                }

                if (occlusion != null)
                {
                    _occlusionMap = MapTextures.LoadOcclusion(occlusion.FilePath, out note);
                    LogMap("occlusion map", occlusion.FileName, _occlusionMap, note);
                }

                if (Plugin.UseParallax.Value && height != null)
                {
                    _heightMap = MapTextures.LoadHeight(height.FilePath, out note);
                    LogMap("height map (parallax)", height.FileName, _heightMap, note);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not load a map: " + ex.Message);
            }
        }

        private static void LogMap(string what, string file, Texture2D texture, string note)
        {
            if (texture == null)
            {
                Plugin.Log.LogWarning(Tag + what + " '" + file + "' not used: " + note);
            }
            else
            {
                Plugin.Log.LogInfo(Tag + what + " '" + file + "': " + texture.width + "x" + texture.height + ", " + note);
            }
        }

        private static void ReleaseMaps()
        {
            if (_normalMap != null)
            {
                UnityEngine.Object.Destroy(_normalMap);
            }

            if (_occlusionMap != null)
            {
                UnityEngine.Object.Destroy(_occlusionMap);
            }

            if (_heightMap != null)
            {
                UnityEngine.Object.Destroy(_heightMap);
            }

            _normalMap = null;
            _occlusionMap = null;
            _heightMap = null;
        }

        private static string MapsSummary()
        {
            string text = "";
            if (_normalMap != null) text += "normal";
            if (_occlusionMap != null) text += (text.Length > 0 ? ", " : "") + "occlusion";
            if (_heightMap != null) text += (text.Length > 0 ? ", " : "") + "height";
            return text.Length > 0 ? text : "none";
        }

        // The shader's own switches for each map: the keyword makes it read the texture, the strength sets how much.
        private static void ConfigureMaterial(Material material, ThemeScript theme)
        {
            SetMap(material, "_NORMALMAP", BumpMapId, _normalMap);
            if (_normalMap != null)
            {
                material.SetFloat(BumpScaleId, Plugin.NormalStrength.Value);
            }

            SetMap(material, "_OCCLUSIONMAP", OcclusionMapId, _occlusionMap);
            if (_occlusionMap != null)
            {
                material.SetFloat(OcclusionStrengthId, Plugin.OcclusionStrength.Value);
            }

            SetMap(material, "_PARALLAXMAP", ParallaxMapId, _heightMap);
            if (_heightMap != null)
            {
                material.SetFloat(ParallaxId, Plugin.ParallaxHeight.Value);
            }

            ApplyConstants(material, theme);
        }

        private static void SetMap(Material material, string keyword, int propertyId, Texture2D texture)
        {
            if (texture != null)
            {
                material.SetTexture(propertyId, texture);
                material.EnableKeyword(keyword);
            }
            else
            {
                material.SetTexture(propertyId, null);
                material.DisableKeyword(keyword);
            }
        }

        // Metallic and smoothness have no map in the part shader: they come from the paint material's entry in
        // _MaterialData (metallic, smoothness, emission day, emission night). The reserved albedo entry of this
        // material instance gets the values chosen in the panel; the craft's own theme is not touched.
        private static void ApplyConstants(Material material, ThemeScript theme)
        {
            int index = AlbedoShaderIndex(theme) * 10 + 3;
            Vector4[] data = material.GetVectorArray(MaterialDataId);
            if (data == null || data.Length <= index)
            {
                return;
            }

            Vector4 values = data[index];
            values.x = Plugin.Metallic.Value;
            values.y = Plugin.Smoothness.Value;
            data[index] = values;
            material.SetVectorArray(MaterialDataId, data);
        }

        /// <summary>Applies the strength, metallic and smoothness settings to the material that is already in use.</summary>
        public static void RefreshMaterial()
        {
            foreach (KeyValuePair<ThemeScript, Material> pair in AtlasMaterials)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                try
                {
                    ConfigureMaterial(pair.Value, pair.Key);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "could not refresh the material: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The theme rewrites _MaterialData of every material it keeps whenever the paint changes; this puts
        /// the metallic and smoothness chosen in the panel back on ours.
        /// </summary>
        public static void OnThemeMaterialUpdated(ThemeScript theme, Material material)
        {
            if (AtlasMaterials.Count == 0)
            {
                return;
            }

            Material atlas;
            if (AtlasMaterials.TryGetValue(theme, out atlas) && atlas == material)
            {
                try
                {
                    ApplyConstants(atlas, theme);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "could not keep the metallic and smoothness: " + ex.Message);
                }
            }
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
                                if (rs.WroteTangents)
                                {
                                    mesh.tangents = null; // takes the tangent channel out again
                                }
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
            _options = null;
            _activeAircraft = null;
            _verifyFrame = -1;
            SetStatus("Removed (" + renderers + " renderers restored).");
            Plugin.Log.LogInfo(Tag + "reverted " + renderers + " renderer(s)");
        }

        private static void ReleaseTexture()
        {
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }

            ReleaseMaps();
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
            SetStatus(message);
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

            Plugin.Log.LogInfo(Tag + "verify: maps | normal " + (atlas.GetTexture(BumpMapId) != null) + " (_NORMALMAP " + atlas.IsKeywordEnabled("_NORMALMAP")
                + "), occlusion " + (atlas.GetTexture(OcclusionMapId) != null) + " (_OCCLUSIONMAP " + atlas.IsKeywordEnabled("_OCCLUSIONMAP")
                + "), height " + (atlas.GetTexture(ParallaxMapId) != null) + " (_PARALLAXMAP " + atlas.IsKeywordEnabled("_PARALLAXMAP")
                + ") | metallic " + F(Plugin.Metallic.Value) + ", smoothness " + F(Plugin.Smoothness.Value));
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
