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
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int ParallaxMapId = Shader.PropertyToID("_ParallaxMap");

        // State of the current application.
        private static Texture2D _texture;
        private static Texture2D _normalMap;
        private static Texture2D _occlusionMap;
        private static Texture2D _heightMap;
        private static bool _specularLoaded;
        private static float _specularAverage;
        private static AtlasMaterial.SpecularKind _specularKind;
        private static Texture2D _metalSmoothMap;   // Lit only: metallic in R, smoothness in A
        private static bool _metalLoaded;
        private static float _metalAverage;
        private static bool _roughLoaded;
        private static float _roughAverage;
        private static string _modelProblem = "";
        private static AtlasLook _look = new AtlasLook();
        private static AircraftScript _activeAircraft;
        private static ObjModel _model;
        private static TriangleBvh _bvh;
        private static ModelAlignment _alignment;
        private static string _modelPath;       // the OBJ in use (null: tiled); the model itself may not be read yet
        private static bool _normalsInverted;   // how the model's faces wind (kept apart: the BVH may not exist yet)
        private static UvCache _uvCache;        // the coordinates of this craft's renderers, by part (see UvCache)
        private static bool _uvCacheDirty;
        private static float _uvDirtySince;
        private static AircraftScript _cacheAircraft;
        private static string _fitQuality = "";
        private static int _cachedRenderers;
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

        // Size of the craft (in its own space) when the alignment was made or taken from a saved file.
        private static Bounds _fitBounds;
        private static bool _hasFitBounds;

        // A saved alignment that the next apply must use instead of searching for one (set when a craft's saved
        // texture is put on again; used once, so that any later change in the panel works from a fresh search).
        private static CraftAssignment _stored;
        private static int _pendingOrientation;
        private static bool _alignmentFromSaved;

        // Opening a craft in the designer: the saved texture of that craft is put on by itself.
        private static AircraftScript _autoAircraft;
        private static string _autoName;
        private static float _autoReadyAt;
        private static bool _autoDone = true;
        private static float _nextAutoCheck;
        private static string _savedName;

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

        // Replaces the row with this label (or adds it).
        private static void StatusSet(string label, string value)
        {
            for (int i = 0; i < StatusRows.Count; i++)
            {
                if (StatusRows[i][0] == label)
                {
                    StatusRows[i][1] = value;
                    return;
                }
            }

            StatusAdd(label, value);
        }

        public static bool IsActive
        {
            get { return Parts.Count > 0; }
        }

        // The texture coordinates come from a model: aligned, with its options (the OBJ itself may still be unread).
        private static bool TransferActive
        {
            get { return _alignment != null && _options != null && _modelPath != null; }
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Called every frame by the plugin; puts a craft's saved texture on when it is opened, and runs the delayed self-check after applying.</summary>
        public static void Tick()
        {
            try
            {
                SaveUvCacheIfDue();
                AutoTick();
            }
            catch (Exception ex)
            {
                if (!_autoErrorLogged)
                {
                    _autoErrorLogged = true;
                    Plugin.Log.LogWarning(Tag + "automatic apply failed (reported once): " + ex);
                }
            }

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

        private static bool _autoErrorLogged;
        private static int _autoTries;

        // ------------------------------------------------------------------ saved texture of a craft

        /// <summary>
        /// Looks (twice a second) at the craft open in the designer. A craft that was just opened and has a saved
        /// texture gets it put on; a craft that has a texture on it and was saved under another name keeps it
        /// under that name too ("Save as").
        /// </summary>
        private static void AutoTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextAutoCheck)
            {
                return;
            }

            _nextAutoCheck = now + 0.5f;

            AircraftScript aircraft = DiagnosticDump.FindDesignerAircraft();
            if (aircraft == null)
            {
                _autoAircraft = null;
                _autoName = null;
                _autoDone = true;
                return;
            }

            Prune();
            string name = DiagnosticDump.CraftNameOf(aircraft);
            bool active = Parts.Count > 0 && _activeAircraft == aircraft;

            if (aircraft != _autoAircraft || name != _autoName)
            {
                // A texture is on the craft and the name changed: the craft was saved under another name ("Save as").
                // Otherwise it is another craft (or one with a new name): look for its saved texture.
                bool savedAs = aircraft == _autoAircraft && active;
                _autoAircraft = aircraft;
                _autoName = name;
                _autoDone = savedAs;
                _autoTries = 0;
                _autoReadyAt = now + 1.5f; // let the designer build the parts of a newly opened craft

                if (savedAs && name.Length > 0 && !string.Equals(name, _savedName, StringComparison.OrdinalIgnoreCase))
                {
                    SaveAssignment(aircraft);
                }
            }

            if (_autoDone || now < _autoReadyAt)
            {
                return;
            }

            if (active || !Plugin.AutoApply.Value || name.Length == 0)
            {
                _autoDone = true;
                return;
            }

            CraftAssignment assignment = CraftAssignment.Load(name);
            if (assignment == null)
            {
                _autoDone = true;
                return;
            }

            if (aircraft.Parts == null || aircraft.Parts.Count == 0)
            {
                _autoReadyAt = now + 1f; // the parts are not built yet
                return;
            }

            _autoDone = true;
            if (!ApplyAssignment(aircraft, name, assignment) && _autoTries < 4)
            {
                // The parts exist but their meshes were not ready: try again in a moment.
                _autoTries++;
                _autoDone = false;
                _autoReadyAt = now + 2f;
            }
        }

        // Puts the texture that was saved for the craft on it again: its settings go into the panel's settings,
        // and its alignment is used as it is (no search).
        private static bool ApplyAssignment(AircraftScript aircraft, string name, CraftAssignment assignment)
        {
            string problem;
            if (!assignment.ApplyToConfig(out problem))
            {
                Plugin.Log.LogWarning(Tag + "the texture saved for the craft '" + name + "' was not put on: " + problem);
                SetStatus("The texture saved for this craft was not put on: " + problem + ".");
                return true; // nothing to wait for: the files are missing
            }

            Plugin.Log.LogInfo(Tag + "the craft '" + name + "' has a saved texture: image '" + assignment.Image + "', model '"
                + (assignment.Model.Length > 0 ? assignment.Model : "none") + "', maps [" + assignment.Normal + "|" + assignment.Occlusion + "|"
                + assignment.Specular + "|" + assignment.Height + "], alignment " + (assignment.HasAlignment ? "saved" : "none"));

            InvalidateFit();
            _stored = assignment;
            _pendingOrientation = Mathf.Max(0, assignment.Orientation);
            bool applied;
            try
            {
                applied = ApplyAll(aircraft, aircraft.LoadContext.ToString(), false);
            }
            finally
            {
                _stored = null;
            }

            if (applied)
            {
                _savedName = name;
            }

            return applied;
        }

        /// <summary>Writes what is on the craft to the craft's saved-texture file (called after the texture or its settings changed).</summary>
        public static void SaveAssignment()
        {
            Prune();
            if (Parts.Count == 0 || _activeAircraft == null)
            {
                return;
            }

            SaveAssignment(_activeAircraft);
        }

        // Remembers what is on the craft under the craft's name, so that opening it in the designer or flying it
        // puts the same texture on again.
        private static void SaveAssignment(AircraftScript aircraft)
        {
            string name = DiagnosticDump.CraftNameOf(aircraft);
            if (name.Length == 0)
            {
                _savedName = null;
                StatusSet("Saved for flight", "no: the craft has no name yet");
                Plugin.Log.LogInfo(Tag + "the craft has no name yet, so the texture is not saved for flight: save the craft, then apply again");
                return;
            }

            TextureLibrary.Entry image = TextureLibrary.CurrentImage;
            if (image == null)
            {
                StatusSet("Saved for flight", "no: no image in the folder");
                return;
            }

            bool transfer = TransferActive;
            CraftAssignment a = new CraftAssignment();
            a.Image = image.FileName;
            a.Model = transfer ? System.IO.Path.GetFileName(_modelPath) : "";
            a.Normal = TextureLibrary.Normal.CurrentName ?? "";
            a.Occlusion = TextureLibrary.Occlusion.CurrentName ?? "";
            a.Height = TextureLibrary.Height.CurrentName ?? "";
            a.Specular = TextureLibrary.Specular.CurrentName ?? "";
            a.MetallicMap = TextureLibrary.Metalness.CurrentName ?? "";
            a.RoughnessMap = TextureLibrary.Roughness.CurrentName ?? "";
            a.UseLit = _look.UseLit;
            a.MetalAverage = _metalLoaded ? _metalAverage : 0f;
            a.RoughAverage = _roughLoaded ? _roughAverage : 0f;
            a.TilesPerMeter = Plugin.TilesPerMeter.Value;
            a.FlipModelX = Plugin.FlipModelX.Value;

            if (transfer)
            {
                a.HasAlignment = true;
                a.ObjToCraft = _alignment.ObjToCraft;
                a.NormalsInverted = _normalsInverted;
                a.FrontFaces = _frontFaces;
                a.Project = _options.Project;
                a.RayStart = _options.RayStartMeters;
                a.FacingWeight = _options.FacingWeightSquareMeters;
                a.MaxDistance = _options.MaxDistanceMeters;
            }

            a.Orientation = _fitChoice;
            a.Offset = new Vector3(Plugin.AdjustOffsetX.Value, Plugin.AdjustOffsetY.Value, Plugin.AdjustOffsetZ.Value);
            a.Rotation = new Vector3(Plugin.AdjustRotationX.Value, Plugin.AdjustRotationY.Value, Plugin.AdjustRotationZ.Value);
            a.AdjustScale = Plugin.AdjustScale.Value;

            a.NormalStrength = Plugin.NormalStrength.Value;
            a.FlipNormalGreen = Plugin.FlipNormalGreen.Value;
            a.RebuildNormalZ = Plugin.RebuildNormalZ.Value;
            a.HeightToNormal = Plugin.HeightToNormal.Value;
            a.BumpStrength = Plugin.BumpStrength.Value;
            a.OcclusionStrength = Plugin.OcclusionStrength.Value;
            a.UseParallax = Plugin.UseParallax.Value;
            a.ParallaxHeight = Plugin.ParallaxHeight.Value;
            a.Metallic = Plugin.Metallic.Value;
            a.Smoothness = Plugin.Smoothness.Value;
            a.SpecularAverage = _specularLoaded ? _specularAverage : 0f;
            a.SpecularGain = Plugin.SpecularGain.Value;
            a.SpecularKind = (int)_specularKind;

            if (_hasFitBounds)
            {
                a.HasBounds = true;
                a.BoundsCenter = _fitBounds.center;
                a.BoundsSize = _fitBounds.size;
            }

            if (a.Save(name))
            {
                _savedName = name;
                StatusSet("Saved for flight", "yes, for the craft '" + name + "'");
                Plugin.Log.LogInfo(Tag + "texture saved for the craft '" + name + "': " + CraftAssignment.FilePathFor(name));
                SaveUvCache(name);
                if (!transfer)
                {
                    try
                    {
                        string uv = CraftAssignment.UvFilePathFor(name);
                        if (System.IO.File.Exists(uv))
                        {
                            System.IO.File.Delete(uv); // coordinates of an earlier model are of no use any more
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning(Tag + "could not delete the old saved coordinates: " + ex.Message);
                    }
                }
            }
            else
            {
                StatusSet("Saved for flight", "no: could not write the file (see the log)");
            }
        }

        // Writes the coordinates cache of the craft when something new is in it.
        private static void SaveUvCache(string craftName)
        {
            if (_uvCache != null && _uvCacheDirty && TransferActive && _uvCache.Count > 0)
            {
                if (_uvCache.Save(craftName))
                {
                    _uvCacheDirty = false;
                }
            }
        }

        // Parts rebuilt or added after the apply add entries one by one: they are written a few seconds after the last one.
        private static void SaveUvCacheIfDue()
        {
            if (_uvCacheDirty && _savedName != null && Time.unscaledTime > _uvDirtySince + 3f && Parts.Count > 0)
            {
                SaveUvCache(_savedName);
            }
        }

        private static void MarkUvCacheDirty()
        {
            if (!_uvCacheDirty)
            {
                _uvDirtySince = Time.unscaledTime;
            }

            _uvCacheDirty = true;
        }

        /// <summary>Applies the selected image (and model) to the aircraft open in the designer; applies again if already applied.</summary>
        public static void ApplyCurrent()
        {
            _uvCache = null; // pressing Apply works the coordinates out again, whatever was saved
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

        /// <summary>Removes the texture from the aircraft, restores its normal paint and forgets the texture saved for the craft.</summary>
        public static void RemoveCurrent()
        {
            Prune();
            AircraftScript aircraft = _activeAircraft != null ? _activeAircraft : DiagnosticDump.FindDesignerAircraft();
            string name = aircraft != null ? DiagnosticDump.CraftNameOf(aircraft) : "";
            bool forgotten = name.Length > 0 && CraftAssignment.Delete(name);
            if (forgotten)
            {
                _savedName = null;
                _uvCache = null;
                _uvCacheDirty = false;
                Plugin.Log.LogInfo(Tag + "the texture saved for the craft '" + name + "' was forgotten");
            }

            if (Parts.Count > 0)
            {
                RevertAll();
                if (forgotten)
                {
                    StatusText += " The texture saved for this craft was forgotten.";
                }
            }
            else
            {
                SetStatus(forgotten ? "Nothing on the craft; the texture saved for it was forgotten." : "Nothing to remove.");
            }
        }

        /// <summary>Forgets the automatic alignment so the next apply finds it again (after the craft changed a lot).</summary>
        public static void InvalidateFit()
        {
            _fitCandidates = null;
            _fitChoice = 0;
            _pendingOrientation = 0;
            _uvCache = null; // a new alignment means new coordinates
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
                    return _alignmentFromSaved ? "saved with the craft (no search)" : "-";
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

        private static bool ApplyAll(AircraftScript aircraft, string context, bool save = true)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            _hasFitBounds = false;
            _alignmentFromSaved = false;
            _modelProblem = "";

            ThemeScript theme = aircraft.Theme;
            if (theme == null || theme.Theme == null || theme.Material == null)
            {
                Fail("The aircraft has no usable theme.");
                return false;
            }

            if (aircraft.LoadContext != CraftLoadContext.Designer)
            {
                Plugin.Log.LogWarning(Tag + "context is " + context + ", not the designer: in flight the game merges most part meshes at load, "
                    + "so only parts that are never merged (wings, flaps) are expected to show the texture. Results here are not representative.");
            }

            TextureLibrary.Entry entry = TextureLibrary.CurrentImage;
            if (!LoadImage(entry))
            {
                return false;
            }

            LoadMaps();
            BuildLook();

            if (_cacheAircraft != aircraft)
            {
                _cacheAircraft = aircraft;
                _uvCache = null; // the coordinates kept are those of another craft
                _uvCacheDirty = false;
            }

            // With the saved alignment and the coordinates saved with it (a craft whose texture is put on again) the
            // OBJ is not even read; it is read later only for a renderer that has no saved coordinates.
            bool transfer = false;
            bool deferred = false;
            string modelPath = TextureLibrary.CurrentModelPath;
            if (modelPath != null)
            {
                deferred = TryUseSavedUvs(aircraft, modelPath);
                transfer = deferred || PrepareModel(modelPath);
                _modelPath = transfer ? modelPath : null;
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
                _modelPath = null;
                _uvCache = null;
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
                return false;
            }

            _activeAircraft = aircraft;

            // 2. Align the model to the craft.
            string fit = "";
            _fitQuality = "";
            if (transfer && !deferred)
            {
                transfer = FitModel(aircraft, candidates, out fit);
                if (!transfer)
                {
                    _modelProblem = "the model could not be aligned to the craft";
                    _modelPath = null;
                    _uvCache = null;
                }
            }

            // The coordinates of an earlier apply stay valid while the alignment and its options are the same
            // (changing the image or a map does not need them again).
            if (transfer)
            {
                string signature = UvCache.MakeSignature(_modelPath, Plugin.FlipModelX.Value, _alignment.ObjToCraft, _normalsInverted,
                    _frontFaces, _options.Project, _options.RayStartMeters, _options.FacingWeightSquareMeters, _options.MaxDistanceMeters);
                if (_uvCache == null || _uvCache.Signature != signature)
                {
                    _uvCache = new UvCache(signature);
                    MarkUvCacheDirty();
                }
            }

            // 3. Write UVs, styles and materials.
            Stats.Reset();
            _cachedRenderers = 0;
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
                ? "model UV transfer (" + System.IO.Path.GetFileName(_modelPath) + ")"
                : "tiled, " + Plugin.TilesPerMeter.Value.ToString("0.##", CultureInfo.InvariantCulture) + " per meter";

            Plugin.Log.LogInfo(Tag + "applied '" + _textureName() + "' to " + renderers + " renderer(s) of " + parts + " part(s) in "
                + watch.ElapsedMilliseconds + " ms | mode: " + mode
                + " | skipped " + skipped + (skippedText.Length > 0 ? " (" + skippedText + ")" : ""));
            string debugNote = "";
            if (transfer && Stats.Vertices > 0)
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

            bool modelFailed = !transfer && TextureLibrary.CurrentModelPath != null;
            StatusText = "Applied to " + renderers + " renderers in " + watch.ElapsedMilliseconds + " ms"
                + (modelFailed ? ". THE MODEL WAS NOT USED, the image is only tiled." : "");
            StatusRows.Clear();
            StatusAdd("Mode", transfer ? "model UV transfer" : "tiled, " + F(Plugin.TilesPerMeter.Value) + " per meter");
            if (modelFailed)
            {
                StatusAdd("Model", "NOT USED: " + (_modelProblem.Length > 0 ? _modelProblem : "see the log"));
            }

            StatusAdd("Maps", MapsSummary());
            if (transfer)
            {
                StatusAdd("Model", System.IO.Path.GetFileName(_modelPath));
                StatusAdd("Orientation", OrientationText);
                if (_fitQuality.Length > 0)
                {
                    StatusAdd("Model fit", _fitQuality);
                }

                if (Stats.Vertices == 0)
                {
                    StatusAdd("Match", "texture coordinates taken from the saved ones (" + _cachedRenderers + " renderers)");
                }
                else
                {
                    StatusAdd("Model faces", _facingShort);
                    StatusAdd("Match", Stats.MatchSummary() + (_cachedRenderers > 0 ? "; " + _cachedRenderers + " renderers from the saved ones" : ""));
                    StatusAdd("Faces up", Stats.KindSummary(0));
                    StatusAdd("Faces down", Stats.KindSummary(1));
                    StatusAdd("Sideways", Stats.KindSummary(2));
                    StatusAdd("Texture coords", Stats.UvRangeSummary());
                }
                if (debugNote.Length > 0)
                {
                    StatusAdd("Debug map", UvDebugMap.FileName);
                }
            }

            StatusAdd("Craft file", "not modified");
            if (save)
            {
                SaveAssignment(aircraft);
            }
            else
            {
                StatusSet("Saved for flight", "yes, put on from the craft's saved file");
            }

            _verifyFrame = Time.frameCount + 5;
            return true;
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

        // A craft that has a saved texture, a saved alignment and saved coordinates made with them: the alignment is
        // taken as it is and the OBJ is left unread (EnsureModel reads it if some renderer needs it).
        private static bool TryUseSavedUvs(AircraftScript aircraft, string modelPath)
        {
            if (_stored == null || !_stored.HasAlignment)
            {
                return false;
            }

            string name = DiagnosticDump.CraftNameOf(aircraft);
            if (name.Length == 0)
            {
                return false;
            }

            UvCache cache = UvCache.Load(name, UvCache.MakeSignature(modelPath, _stored));
            if (cache == null)
            {
                return false;
            }

            _uvCache = cache;
            _uvCacheDirty = false;
            _model = null;
            _bvh = null;
            ApplyStoredState(_stored);
            if (_stored.HasBounds)
            {
                _fitBounds = new Bounds(_stored.BoundsCenter, _stored.BoundsSize);
                _hasFitBounds = true;
            }

            Plugin.Log.LogInfo(Tag + "the OBJ is not read: the craft has saved texture coordinates for " + cache.Count + " renderer(s)");
            return true;
        }

        // The model is read (if it was not) for a renderer whose coordinates have to be worked out.
        private static bool EnsureModel()
        {
            if (_model != null && _bvh != null)
            {
                return true;
            }

            if (_modelPath == null)
            {
                return false;
            }

            ObjModel model;
            TriangleBvh bvh;
            string error;
            if (!ModelCache.Get(_modelPath, Plugin.FlipModelX.Value, out model, out bvh, out error))
            {
                Plugin.Log.LogWarning(Tag + "a renderer needs the model but it could not be read: " + error);
                return false;
            }

            _model = model;
            _bvh = bvh;
            _bvh.SetNormalsInverted(_normalsInverted);
            return true;
        }

        // Reads and indexes the OBJ (cached while the file and the mirror setting do not change).
        private static bool PrepareModel(string path)
        {
            ObjModel model;
            TriangleBvh bvh;
            string error;
            if (!ModelCache.Get(path, Plugin.FlipModelX.Value, out model, out bvh, out error))
            {
                _modelProblem = error;
                SetStatus("Model not used: " + error);
                _model = null;
                _bvh = null;
                return false;
            }

            _model = model;
            _bvh = bvh;
            return true;
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
                    Matrix4x4 toCraft = UvProjection.RendererToCraft(c.Map.Renderer.transform, aircraft.transform);
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
                _fitBounds = bounds;
                _hasFitBounds = true;

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

                // The alignment saved with the craft is used as it is: nothing is searched.
                if (_stored != null && _stored.HasAlignment)
                {
                    UseStoredAlignment(_stored, bounds, out summary);
                    return true;
                }

                bool reused = _fitCandidates != null && _fitCandidates.Count > 0 && _fitAircraft == aircraft
                    && _fitModelPath == ModelCache.LoadedPath && _fitFlip == ModelCache.LoadedFlip;
                if (!reused)
                {
                    _fitCandidates = ModelAlignment.FitCandidates(_model, _bvh, samples, bounds);
                    _fitChoice = Mathf.Clamp(_pendingOrientation, 0, _fitCandidates.Count - 1);
                    _pendingOrientation = 0;
                    _fitAircraft = aircraft;
                    _fitModelPath = ModelCache.LoadedPath;
                    _fitFlip = ModelCache.LoadedFlip;

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

                _normalsInverted = _bvh.NormalsInverted;

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
                float craftMax = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                float percent = 100f * auto.MeanDistance / Mathf.Max(craftMax, 1e-4f);
                if (auto.MeanDistance > 0.08f * craftMax)
                {
                    _fitQuality = "POOR, " + F(percent) + "% off: is the craft in the model's pose (swept wings, gear...)?";
                    Plugin.Log.LogWarning(Tag + "the model does not fit the craft well (mean distance " + F(auto.MeanDistance) + " m, " + F(percent)
                        + "% of its size). Is it the same aircraft in the same pose (wing sweep, flaps, gear)? Put the craft in the model's pose, "
                        + "apply, then pose it again: the coordinates stay with the meshes. Or try the next orientation, the Adjust settings or FlipModelX = false.");
                }
                else
                {
                    _fitQuality = "good, " + F(percent) + "% off";
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

        // The alignment saved with the craft, as state (the BVH, if there is one, is told separately).
        private static void ApplyStoredState(CraftAssignment stored)
        {
            _alignment = ModelAlignment.FromMatrix(stored.ObjToCraft);
            _normalsInverted = stored.NormalsInverted;
            _frontFaces = stored.FrontFaces;
            _alignmentFromSaved = true;
            _fitCandidates = null; // a change made later in the panel searches again
            _fitChoice = 0;
            _facingShort = "saved" + (stored.NormalsInverted ? " (turned around)" : "");
            _fitQuality = "saved with the craft";

            _options = new UvTransfer.Options
            {
                Project = stored.Project,
                RayStartMeters = stored.RayStart,
                MaxDistanceMeters = stored.MaxDistance,
                FrontFacesOnly = _frontFaces,
                FacingWeightSquareMeters = stored.FacingWeight,
                FarLimitMeters = stored.MaxDistance
            };
        }

        // The model-to-craft matrix saved with the craft is the final alignment (the search and the manual
        // adjustment together): it is used as it is. Whether the craft still looks like it did is only reported.
        private static void UseStoredAlignment(CraftAssignment stored, Bounds bounds, out string summary)
        {
            ApplyStoredState(stored);
            _bvh.SetNormalsInverted(stored.NormalsInverted);

            summary = "alignment taken from the craft's saved file";
            Plugin.Log.LogInfo(Tag + "craft size (" + F(bounds.size.x) + ", " + F(bounds.size.y) + ", " + F(bounds.size.z)
                + ") | alignment: " + summary + " | scale " + F(_alignment.Scale) + " | method: "
                + (_options.Project ? "project along normals" : "nearest surface") + ", front faces only: " + _frontFaces);

            if (stored.HasBounds)
            {
                float size = Mathf.Max(stored.BoundsSize.x, Mathf.Max(stored.BoundsSize.y, stored.BoundsSize.z));
                float moved = (bounds.center - stored.BoundsCenter).magnitude;
                float changed = (bounds.size - stored.BoundsSize).magnitude;
                if (moved > 0.05f * size || changed > 0.05f * size)
                {
                    Plugin.Log.LogWarning(Tag + "the craft is not the same as when the texture was applied (its center moved "
                        + F(moved) + " m, its size changed " + F(changed) + " m). If the texture looks shifted, press Align again.");
                }
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

        internal static bool IsEligible(ThemeScript theme, PartMaterialScript.RendererMaterialMap map, out string reason)
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
                if (TransferActive)
                {
                    int partId = state.Part.Part.Id;
                    int ordinal = state.Materials.RendererMaps.IndexOf(map);
                    if (_uvCache != null && _uvCache.Apply(partId, ordinal, mesh))
                    {
                        _cachedRenderers++;
                    }
                    else
                    {
                        if (!EnsureModel())
                        {
                            return false;
                        }

                        UvTransfer.Write(mesh, map.Renderer.transform, aircraft.transform, _model, _bvh, _alignment, _options, Stats);
                        if (_uvCache != null)
                        {
                            _uvCache.Remember(partId, ordinal, mesh);
                            MarkUvCacheDirty();
                        }
                    }
                }
                else
                {
                    UvProjection.BoxProject(mesh, map.Renderer.transform, aircraft.transform, Plugin.TilesPerMeter.Value);
                }

                if (!UvProjection.SetMaterialIndex(mesh, AtlasMaterial.AlbedoShaderIndex(theme)))
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
                try
                {
                    if (rs.OriginalHighlight != null)
                    {
                        map.HighlightMaterials = Fill(count, theme.InitializeHighlightMaterial(atlas));
                    }

                    if (rs.OriginalSelected != null)
                    {
                        map.SelectedMaterials = Fill(count, theme.InitializeHighlightSelectedMaterial(atlas));
                    }
                }
                catch (Exception ex)
                {
                    // The part keeps the game's own highlight (it shows the plain paint while hovered).
                    Plugin.Log.LogWarning(Tag + "highlight material not made: " + ex.Message);
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
            material = AtlasMaterial.Create(theme, _look);
            if (material == null)
            {
                Plugin.Log.LogWarning(Tag + "the theme did not give a material instance");
                return null;
            }

            AtlasMaterials[theme] = material;
            return material;
        }

        // ------------------------------------------------------------------ maps and material values

        // Loads the normal, occlusion, specular and height maps chosen in the panel (any of them may be missing).
        private static void LoadMaps()
        {
            ReleaseMaps();

            TextureLibrary.Entry normal = TextureLibrary.Normal.Current;
            TextureLibrary.Entry occlusion = TextureLibrary.Occlusion.Current;
            TextureLibrary.Entry specular = TextureLibrary.Specular.Current;
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

                if (specular != null)
                {
                    float average;
                    float bright;
                    if (MapTextures.AnalyzeSpecular(specular.FilePath, out average, out bright, out note))
                    {
                        _specularLoaded = true;
                        _specularAverage = average;
                        _specularKind = AtlasMaterial.KindOf(specular.FileName);
                        float addMetallic = 0f;
                        float addSmoothness = 0f;
                        AtlasMaterial.FromSpecularMap(_specularKind, average, Plugin.SpecularGain.Value, true, ref addMetallic, ref addSmoothness);
                        Plugin.Log.LogInfo(Tag + "specular map '" + specular.FileName + "': " + note + ". Taken as a " + _specularKind.ToString().ToLowerInvariant()
                            + " map; the part shader has no such map, so its average adds " + F(addMetallic) + " to the metallic and "
                            + F(addSmoothness) + " to the smoothness (whole craft).");
                    }
                    else
                    {
                        Plugin.Log.LogWarning(Tag + "specular map '" + specular.FileName + "' not used: " + note);
                    }
                }

                LoadMetalRough();
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
            _specularLoaded = false;
            _specularAverage = 0f;
            if (_metalSmoothMap != null)
            {
                UnityEngine.Object.Destroy(_metalSmoothMap);
            }

            _metalSmoothMap = null;
            _metalLoaded = false;
            _roughLoaded = false;
            _metalAverage = 0f;
            _roughAverage = 0f;
        }

        // The metalness and roughness images (each a channel of an image: see MapTextures.ChannelFor). With the URP Lit shader
        // they are packed into one per-pixel map; otherwise only their averages are used (one value for the whole craft).
        private static void LoadMetalRough()
        {
            TextureLibrary.Entry metal = TextureLibrary.Metalness.Current;
            TextureLibrary.Entry rough = TextureLibrary.Roughness.Current;
            if (metal == null && rough == null)
            {
                return;
            }

            byte[] m = null;
            byte[] r = null;
            int mw = 0, mh = 0, rw = 0, rh = 0;
            string note;
            if (metal != null)
            {
                m = MapTextures.ReadChannel(metal.FilePath, MapTextures.ChannelFor(metal.FileName, true), out mw, out mh, out note);
                if (m != null)
                {
                    _metalLoaded = true;
                    _metalAverage = MapTextures.Average(m);
                    Plugin.Log.LogInfo(Tag + "metalness map '" + metal.FileName + "': " + note);
                }
                else
                {
                    Plugin.Log.LogWarning(Tag + "metalness map '" + metal.FileName + "' not used: " + note);
                }
            }

            if (rough != null)
            {
                r = MapTextures.ReadChannel(rough.FilePath, MapTextures.ChannelFor(rough.FileName, false), out rw, out rh, out note);
                if (r != null)
                {
                    _roughLoaded = true;
                    _roughAverage = MapTextures.Average(r);
                    Plugin.Log.LogInfo(Tag + "roughness map '" + rough.FileName + "': " + note);
                }
                else
                {
                    Plugin.Log.LogWarning(Tag + "roughness map '" + rough.FileName + "' not used: " + note);
                }
            }

            if (Plugin.UseLitShader.Value && AtlasMaterial.LitShader != null && (m != null || r != null))
            {
                _metalSmoothMap = MapTextures.PackMetalSmooth(m, mw, mh, r, rw, rh, Plugin.Metallic.Value, Plugin.Smoothness.Value);
                Plugin.Log.LogInfo(Tag + "metallic (R) and smoothness (A) packed for the URP Lit shader" + (_metalSmoothMap != null
                    ? ": " + _metalSmoothMap.width + "x" + _metalSmoothMap.height : " failed"));
            }
        }

        private static string MapsSummary()
        {
            string text = "";
            if (_normalMap != null) text += "normal";
            if (_occlusionMap != null) text += (text.Length > 0 ? ", " : "") + "occlusion";
            if (_specularLoaded) text += (text.Length > 0 ? ", " : "") + _specularKind.ToString().ToLowerInvariant() + " (average only)";
            if (_heightMap != null) text += (text.Length > 0 ? ", " : "") + "height";
            if (_metalLoaded) text += (text.Length > 0 ? ", " : "") + "metalness" + (_metalSmoothMap != null ? " (per pixel)" : " (average)");
            if (_roughLoaded) text += (text.Length > 0 ? ", " : "") + "roughness" + (_metalSmoothMap != null ? " (per pixel)" : " (average)");
            if (_look.UseLit) text += " | URP Lit";
            return text.Length > 0 ? text : "none";
        }

        // What the material shows: the images just loaded and the settings of the panel.
        private static void BuildLook()
        {
            _look.Albedo = _texture;
            _look.Normal = _normalMap;
            _look.Occlusion = _occlusionMap;
            _look.Height = _heightMap;
            _look.MetalSmooth = _metalSmoothMap;
            _look.UseLit = Plugin.UseLitShader.Value && AtlasMaterial.LitShader != null;
            UpdateLookValues();
        }

        // The strengths, metallic and smoothness (the specular map's average adds to the smoothness).
        private static void UpdateLookValues()
        {
            _look.NormalStrength = Plugin.NormalStrength.Value;
            _look.OcclusionStrength = Plugin.OcclusionStrength.Value;
            _look.ParallaxHeight = Plugin.ParallaxHeight.Value;
            float metallic = Plugin.Metallic.Value;
            float smoothness = Plugin.Smoothness.Value;
            AtlasMaterial.FromSpecularMap(_specularKind, _specularAverage, Plugin.SpecularGain.Value, _specularLoaded, ref metallic, ref smoothness);
            if (_metalLoaded) metallic = Mathf.Clamp01(metallic + _metalAverage);
            if (_roughLoaded) smoothness = Mathf.Clamp01(smoothness + 1f - _roughAverage);
            _look.Metallic = metallic;
            _look.Smoothness = smoothness;
        }

        /// <summary>Applies the strength, metallic and smoothness settings to the material that is already in use.</summary>
        public static void RefreshMaterial()
        {
            UpdateLookValues();
            foreach (KeyValuePair<ThemeScript, Material> pair in AtlasMaterials)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                try
                {
                    AtlasMaterial.Update(pair.Value, pair.Key, _look);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "could not refresh the material: " + ex.Message);
                }
            }
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
                    AtlasMaterial.Release(pair.Key, pair.Value);
                }
            }

            AtlasMaterials.Clear();
            ReleaseTexture();

            _model = null;
            _bvh = null;
            _alignment = null;
            _options = null;
            _modelPath = null;
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
            AtlasMaterial.Prune();

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
