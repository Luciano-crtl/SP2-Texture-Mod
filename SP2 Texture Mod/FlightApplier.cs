using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.MeshGen;
using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Puts a craft's saved texture on it when it is loaded for flight (also a test flight from the designer).
    /// In flight the game merges the meshes of most parts into one mesh per group of parts when the group
    /// initializes (PartGroupScript.Initialize). A patch on that method calls OnGroupInitializing first, which
    /// writes the texture coordinates of the group's procedural parts, gives them the texture's material and marks
    /// them "exclude from mesh combine", so the game leaves them as separate renderers. Wings and flaps are never
    /// merged anyway. The texture, the maps and the model come from the files named in the craft's saved file,
    /// and the model alignment is the one saved with it: nothing is searched in flight.
    /// Only the player's own (not remote) aircraft are touched, and the craft file is never changed.
    /// </summary>
    internal static class FlightApplier
    {
        private const string Tag = "[FLIGHT] ";

        private sealed class Session
        {
            public AircraftScript Aircraft;
            public ThemeScript Theme;
            public string CraftName = "";
            public CraftAssignment Assignment;
            public bool Active;          // false: nothing is put on this aircraft
            public AtlasLook Look = new AtlasLook();
            public Material Material;
            public string ModelPath;      // the OBJ (read only if some renderer has no saved coordinates)
            public ObjModel Model;
            public TriangleBvh Bvh;
            public bool ModelFailed;
            public UvCache Cache;
            public int FromCache;
            public int Computed;
            public ModelAlignment Alignment;
            public UvTransfer.Options Options;
            public readonly UvTransfer.Stats Stats = new UvTransfer.Stats();
            public readonly HashSet<PartScript> Done = new HashSet<PartScript>();
            public readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();
            public readonly List<Renderer> Sample = new List<Renderer>();
            public int Parts;
            public int Renderers;
            public int Failed;
            public long Milliseconds;
            public float VerifyAt = -1f;
        }

        private static readonly Dictionary<AircraftScript, Session> Sessions = new Dictionary<AircraftScript, Session>();
        private static float _nextCheck;

        // ------------------------------------------------------------------ entry points

        /// <summary>
        /// Called (by a patch) just before a group of parts looks at its meshes to merge them. The first group of an
        /// aircraft starts the session: the saved texture is looked up and its files are read.
        /// </summary>
        public static void OnGroupInitializing(PartGroupScript group)
        {
            BodyScript body = group.Body;
            AircraftScript aircraft = body != null ? body.Aircraft : null;
            if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft)
            {
                return;
            }

            Session session;
            if (!Sessions.TryGetValue(aircraft, out session))
            {
                session = Begin(aircraft);
                Sessions[aircraft] = session;
            }

            if (!session.Active)
            {
                return;
            }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            ApplyGroup(session, group);
            session.Milliseconds += watch.ElapsedMilliseconds;
            session.VerifyAt = Time.unscaledTime + 2f;
        }

        /// <summary>A procedural part rebuilt its mesh during flight (UV0 and UV1 are rewritten): the texture goes on again.</summary>
        public static void OnMeshRebuilt(ProceduralPartMeshRenderer procedural)
        {
            if (Sessions.Count == 0)
            {
                return;
            }

            MeshRenderer meshRenderer = procedural.MeshRenderer;
            if (meshRenderer == null)
            {
                return;
            }

            PartScript part = meshRenderer.GetComponentInParent<PartScript>();
            if (part == null || part.Aircraft == null || part.PartMaterialScript == null)
            {
                return;
            }

            Session session;
            if (!Sessions.TryGetValue(part.Aircraft, out session) || !session.Active || !session.Done.Contains(part))
            {
                return;
            }

            foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
            {
                string reason;
                if (map.Renderer == meshRenderer && AtlasApplier.IsEligible(session.Theme, map, out reason))
                {
                    ApplyRenderer(session, part, map);
                }
            }

            part.PartMaterialScript.RestoreOriginalMaterials();
        }

        /// <summary>Runs every frame: logs the check of a finished session and frees what belongs to aircraft that are gone.</summary>
        public static void Tick()
        {
            if (Sessions.Count == 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextCheck)
            {
                return;
            }

            _nextCheck = now + 0.5f;

            try
            {
                CheckSessions(now);
            }
            catch (Exception ex)
            {
                if (!_tickErrorLogged)
                {
                    _tickErrorLogged = true;
                    Plugin.Log.LogWarning(Tag + "session check failed (reported once): " + ex);
                }
            }
        }

        private static bool _tickErrorLogged;

        private static void CheckSessions(float now)
        {
            List<AircraftScript> gone = new List<AircraftScript>();
            foreach (KeyValuePair<AircraftScript, Session> pair in Sessions)
            {
                if (pair.Key == null)
                {
                    gone.Add(pair.Key);
                    continue;
                }

                Session session = pair.Value;
                if (session.Active && session.VerifyAt >= 0f && now >= session.VerifyAt)
                {
                    session.VerifyAt = -1f;
                    try
                    {
                        Verify(session);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning(Tag + "verify failed: " + ex.Message);
                    }
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                Session session;
                if (Sessions.TryGetValue(gone[i], out session))
                {
                    Release(session);
                    Sessions.Remove(gone[i]);
                }
            }

            AtlasMaterial.Prune();
        }

        // ------------------------------------------------------------------ the session of one aircraft

        // Looks up the craft's saved texture and reads its files. Active = false when there is nothing to put on.
        private static Session Begin(AircraftScript aircraft)
        {
            Session session = new Session();
            session.Aircraft = aircraft;
            session.Theme = aircraft.Theme;

            try
            {
                if (!Plugin.AutoApply.Value)
                {
                    return session;
                }

                string name = DiagnosticDump.CraftNameOf(aircraft);
                if (name.Length == 0)
                {
                    return session;
                }

                CraftAssignment assignment = CraftAssignment.Load(name);
                if (assignment == null)
                {
                    Plugin.Log.LogInfo(Tag + "the craft '" + name + "' has no saved texture");
                    return session;
                }

                session.CraftName = name;
                session.Assignment = assignment;

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                string problem;
                if (!Prepare(session, out problem))
                {
                    Plugin.Log.LogWarning(Tag + "the texture saved for the craft '" + name + "' was not put on: " + problem);
                    Release(session);
                    return session;
                }

                session.Milliseconds = watch.ElapsedMilliseconds;
                session.Active = true;
                Plugin.Log.LogInfo(Tag + "the craft '" + name + "' has a saved texture: image '" + assignment.Image + "', "
                    + (session.Alignment != null ? "model '" + assignment.Model + "'" : "tiled")
                    + ", maps " + MapsText(session.Look) + ", files read in " + watch.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not prepare the saved texture: " + ex);
                Release(session);
                session.Active = false;
            }

            return session;
        }

        // Reads the images and the model named in the assignment and makes the material.
        private static bool Prepare(Session s, out string problem)
        {
            problem = null;
            CraftAssignment a = s.Assignment;

            ThemeScript theme = s.Theme;
            if (theme == null || theme.Theme == null || theme.Material == null)
            {
                problem = "the aircraft has no usable theme";
                return false;
            }

            string imagePath = TextureLibrary.PathFor(a.Image);
            if (imagePath == null)
            {
                problem = "the image '" + a.Image + "' is not in " + TextureLibrary.Folder;
                return false;
            }

            Texture2D albedo = TextureLibrary.LoadTexture(imagePath, a.Image, false);
            if (albedo == null)
            {
                problem = "the image '" + a.Image + "' could not be read";
                return false;
            }

            // Repeat: tiling needs it, and the model's texture coordinates may lie a little outside [0, 1].
            albedo.wrapMode = TextureWrapMode.Repeat;
            s.Look.Albedo = albedo;

            // The model: with a model the texture coordinates are copied from it; without one the image is tiled.
            if (a.Model.Length > 0)
            {
                string modelPath = TextureLibrary.PathFor(a.Model);
                if (modelPath == null)
                {
                    problem = "the model '" + a.Model + "' is not in " + TextureLibrary.Folder;
                    return false;
                }

                if (!a.HasAlignment)
                {
                    problem = "no alignment of the model is saved: apply the texture again in the designer";
                    return false;
                }

                // The OBJ is only read if some renderer has no saved coordinates (EnsureModel): with the coordinates saved when
                // the texture was applied, flight needs neither the OBJ (its read took 23 s for 380 thousand triangles) nor a search.
                s.ModelPath = modelPath;
                s.Cache = UvCache.Load(s.CraftName, UvCache.MakeSignature(modelPath, a));
                s.Alignment = ModelAlignment.FromMatrix(a.ObjToCraft);
                s.Options = new UvTransfer.Options
                {
                    Project = a.Project,
                    RayStartMeters = a.RayStart,
                    MaxDistanceMeters = a.MaxDistance,
                    FrontFacesOnly = a.FrontFaces,
                    FacingWeightSquareMeters = a.FacingWeight,
                    FarLimitMeters = a.MaxDistance,
                    NeutralBeyondMeters = a.NeutralBeyond,
                    NeutralUv = new Vector2(a.NeutralU, a.NeutralV)
                };
            }

            // The other maps are optional: one that cannot be read is left out.
            string normalPath = TextureLibrary.PathFor(a.Normal);
            string occlusionPath = TextureLibrary.PathFor(a.Occlusion);
            string heightPath = TextureLibrary.PathFor(a.Height);
            string note;
            try
            {
                if (normalPath != null)
                {
                    s.Look.Normal = MapTextures.LoadNormal(normalPath, a.FlipNormalGreen, a.RebuildNormalZ, a.BumpStrength, out note);
                    LogMap("normal map", a.Normal, s.Look.Normal, note);
                }
                else if (a.HeightToNormal && heightPath != null)
                {
                    s.Look.Normal = MapTextures.LoadNormalFromHeight(heightPath, a.BumpStrength, out note);
                    LogMap("normal map from the height map", a.Height, s.Look.Normal, note);
                }

                if (occlusionPath != null)
                {
                    s.Look.Occlusion = MapTextures.LoadOcclusion(occlusionPath, out note);
                    LogMap("occlusion map", a.Occlusion, s.Look.Occlusion, note);
                }

                if (a.UseParallax && heightPath != null)
                {
                    s.Look.Height = MapTextures.LoadHeight(heightPath, out note);
                    LogMap("height map (parallax)", a.Height, s.Look.Height, note);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not read a map: " + ex.Message);
            }

            s.Look.NormalStrength = a.NormalStrength;
            s.Look.OcclusionStrength = a.OcclusionStrength;
            s.Look.ParallaxHeight = a.ParallaxHeight;
            float metallic = a.Metallic;
            float smoothness = a.Smoothness;
            AtlasMaterial.FromSpecularMap((AtlasMaterial.SpecularKind)a.SpecularKind, a.SpecularAverage, a.SpecularGain,
                a.Specular.Length > 0, ref metallic, ref smoothness);
            if (a.MetallicMap.Length > 0) metallic = Mathf.Clamp01(metallic + a.MetalAverage);
            if (a.RoughnessMap.Length > 0) smoothness = Mathf.Clamp01(smoothness + 1f - a.RoughAverage);

            // The URP Lit shader reads metallic and smoothness per pixel: the two images are packed into one map.
            s.Look.UseLit = a.UseLit;
            if (a.UseLit && (a.MetallicMap.Length > 0 || a.RoughnessMap.Length > 0))
            {
                try
                {
                    string metalPath = TextureLibrary.PathFor(a.MetallicMap);
                    string roughPath = TextureLibrary.PathFor(a.RoughnessMap);
                    byte[] m = null;
                    byte[] r = null;
                    int mw = 0, mh = 0, rw = 0, rh = 0;
                    if (metalPath != null) m = MapTextures.ReadChannel(metalPath, MapTextures.ChannelFor(a.MetallicMap, true), out mw, out mh, out note);
                    if (roughPath != null) r = MapTextures.ReadChannel(roughPath, MapTextures.ChannelFor(a.RoughnessMap, false), out rw, out rh, out note);
                    s.Look.MetalSmooth = MapTextures.PackMetalSmooth(m, mw, mh, r, rw, rh, a.Metallic, a.Smoothness);
                    Plugin.Log.LogInfo(Tag + "metallic and smoothness maps packed for the URP Lit shader: "
                        + (s.Look.MetalSmooth != null ? s.Look.MetalSmooth.width + "x" + s.Look.MetalSmooth.height : "failed"));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(Tag + "could not read the metalness/roughness maps: " + ex.Message);
                }
            }

            s.Look.Metallic = metallic;
            s.Look.Smoothness = smoothness;

            s.Material = AtlasMaterial.Create(theme, s.Look);
            if (s.Material == null)
            {
                problem = "the theme did not give a material instance";
                return false;
            }

            return true;
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

        private static string MapsText(AtlasLook look)
        {
            string text = "";
            if (look.Normal != null) text += "normal";
            if (look.Occlusion != null) text += (text.Length > 0 ? ", " : "") + "occlusion";
            if (look.Height != null) text += (text.Length > 0 ? ", " : "") + "height";
            return text.Length > 0 ? text : "none";
        }

        // Reads the OBJ the first time a renderer has no saved coordinates, and checks where the craft is (the alignment is
        // in the craft's own space, which is only needed when coordinates are worked out).
        private static bool EnsureModel(Session s)
        {
            if (s.Model != null)
            {
                return true;
            }

            if (s.ModelFailed || s.ModelPath == null)
            {
                return false;
            }

            ObjModel model;
            TriangleBvh bvh;
            string error;
            if (!ModelCache.Get(s.ModelPath, s.Assignment.FlipModelX, out model, out bvh, out error))
            {
                s.ModelFailed = true;
                Plugin.Log.LogWarning(Tag + "a renderer has no saved texture coordinates and the model could not be read: " + error);
                return false;
            }

            bvh.SetNormalsInverted(s.Assignment.NormalsInverted);
            s.Model = model;
            s.Bvh = bvh;
            CheckFrame(s);
            return true;
        }

        // ------------------------------------------------------------------ the craft's own space in flight

        // The matrix that takes a renderer's mesh to the craft's own space (the one the saved alignment is in).
        // In flight the bodies are moved to where the aircraft spawns, so the chain of transforms up to the aircraft
        // object (what the designer uses) is off by the spawn position (the first flight log showed it: ~1900 m).
        // The part's pose in the craft file (Position, Rotation: how the game placed it at load) does not move, and
        // the renderer's pose relative to its part does not either.
        private static Matrix4x4 RendererToCraft(PartScript part, Renderer renderer)
        {
            PartData data = part.Part;
            Matrix4x4 partToCraft = Matrix4x4.TRS(data.Position, Quaternion.Euler(data.Rotation), part.transform.localScale);
            Matrix4x4 rendererToPart = part.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            return partToCraft * rendererToPart;
        }

        // Size and center of everything that will be textured, in the craft's own space.
        private static bool MeasureBounds(Session s, out Bounds bounds)
        {
            bounds = new Bounds();
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            List<Vector3> vertices = new List<Vector3>();
            foreach (PartData data in s.Aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || part.PartMaterialScript == null)
                {
                    continue;
                }

                foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                {
                    string reason;
                    if (!AtlasApplier.IsEligible(s.Theme, map, out reason))
                    {
                        continue;
                    }

                    Matrix4x4 toCraft = RendererToCraft(part, map.Renderer);
                    vertices.Clear();
                    map.Mesh.GetVertices(vertices);
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 p = toCraft.MultiplyPoint3x4(vertices[i]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        any = true;
                    }
                }
            }

            if (any)
            {
                bounds.SetMinMax(min, max);
            }

            return any;
        }

        // The craft's size and place are compared with what they were when the alignment was made. The same size at
        // another place means the frame is shifted (the alignment is moved by the same amount); another size means the
        // craft was changed after the texture was applied (reported only).
        private static void CheckFrame(Session s)
        {
            CraftAssignment a = s.Assignment;
            Bounds bounds;
            if (!MeasureBounds(s, out bounds))
            {
                return;
            }

            Transform root = s.Aircraft.transform;
            string where = "craft size (" + F(bounds.size.x) + ", " + F(bounds.size.y) + ", " + F(bounds.size.z) + "), center ("
                + F(bounds.center.x) + ", " + F(bounds.center.y) + ", " + F(bounds.center.z) + "), aircraft object at ("
                + F(root.position.x) + ", " + F(root.position.y) + ", " + F(root.position.z) + ")";
            if (!a.HasBounds || s.Alignment == null)
            {
                Plugin.Log.LogInfo(Tag + where + " (nothing saved to compare with)");
                return;
            }

            float size = Mathf.Max(a.BoundsSize.x, Mathf.Max(a.BoundsSize.y, a.BoundsSize.z));
            Vector3 shift = bounds.center - a.BoundsCenter;
            float changed = (bounds.size - a.BoundsSize).magnitude;
            if (changed > 0.05f * size)
            {
                Plugin.Log.LogWarning(Tag + where + ": the craft is not the size it had when the texture was applied (saved size ("
                    + F(a.BoundsSize.x) + ", " + F(a.BoundsSize.y) + ", " + F(a.BoundsSize.z) + ")). If the texture looks wrong, apply it again in the designer.");
            }
            else if (shift.magnitude > 0.02f * size)
            {
                s.Alignment = ModelAlignment.FromMatrix(Matrix4x4.Translate(shift) * a.ObjToCraft);
                Plugin.Log.LogWarning(Tag + where + ": the craft is " + F(shift.magnitude) + " m away from where it was when the texture was applied; the alignment was moved by the same amount");
            }
            else
            {
                Plugin.Log.LogInfo(Tag + where + ": same size and place as when the texture was applied");
            }
        }

        // ------------------------------------------------------------------ putting it on

        private static void ApplyGroup(Session s, PartGroupScript group)
        {
            foreach (PartScript part in group.Parts)
            {
                if (part == null || part.PartMaterialScript == null || !s.Done.Add(part))
                {
                    continue;
                }

                bool any = false;
                foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                {
                    string reason;
                    if (!AtlasApplier.IsEligible(s.Theme, map, out reason))
                    {
                        int n;
                        s.Skipped.TryGetValue(reason, out n);
                        s.Skipped[reason] = n + 1;
                        continue;
                    }

                    if (ApplyRenderer(s, part, map))
                    {
                        s.Renderers++;
                        any = true;
                        if (s.Sample.Count < 3)
                        {
                            s.Sample.Add(map.Renderer);
                        }
                    }
                    else
                    {
                        s.Failed++;
                    }
                }

                if (any)
                {
                    s.Parts++;

                    // The renderers still hold the part's own material: this gives them the one in the map.
                    part.PartMaterialScript.RestoreOriginalMaterials();
                }
            }
        }

        // The same steps as in the designer, without anything to undo: texture coordinates, the albedo paint
        // style in UV1.x, tangents for the normal map, the texture's material, and "do not merge".
        private static bool ApplyRenderer(Session s, PartScript part, PartMaterialScript.RendererMaterialMap map)
        {
            try
            {
                Mesh mesh = map.Mesh;
                if (s.Alignment != null)
                {
                    int ordinal = part.PartMaterialScript.RendererMaps.IndexOf(map);
                    if (s.Cache != null && s.Cache.Apply(part.Part.Id, ordinal, mesh))
                    {
                        s.FromCache++;
                    }
                    else
                    {
                        if (!EnsureModel(s))
                        {
                            return false;
                        }

                        UvTransfer.Write(mesh, RendererToCraft(part, map.Renderer), s.Model, s.Bvh, s.Alignment, s.Options, s.Stats);
                        s.Stats.Points.Clear(); // only the designer draws the debug map
                        s.Computed++;
                    }
                }
                else
                {
                    UvProjection.BoxProject(mesh, RendererToCraft(part, map.Renderer), s.Assignment.TilesPerMeter);
                }

                if (!UvProjection.SetMaterialIndex(mesh, AtlasMaterial.AlbedoShaderIndex(s.Theme)))
                {
                    Plugin.Log.LogWarning(Tag + "mesh '" + mesh.name + "' has no usable UV1");
                    return false;
                }

                if (s.Look.Normal != null || s.Look.Height != null)
                {
                    mesh.RecalculateTangents();
                }

                map.OriginalMaterials = Fill(map.OriginalMaterials.Length, s.Material);
                map.ExcludeFromMeshCombine = true;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "could not texture a renderer: " + ex);
                return false;
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

        // ------------------------------------------------------------------ check and cleanup

        // A moment after the groups initialized: what the renderers really use now (written to the log).
        private static void Verify(Session s)
        {
            System.Text.StringBuilder skipped = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in s.Skipped)
            {
                if (skipped.Length > 0)
                {
                    skipped.Append("; ");
                }

                skipped.Append(pair.Key).Append(" x").Append(pair.Value);
            }

            Plugin.Log.LogInfo(Tag + "texture of the craft '" + s.CraftName + "' put on " + s.Renderers + " renderer(s) of " + s.Parts
                + " part(s) in " + s.Milliseconds + " ms (including reading its files) | not textured: " + s.Failed + " failed, skipped "
                + (skipped.Length > 0 ? skipped.ToString() : "none")
                + (s.Alignment != null ? " | texture coordinates: " + s.FromCache + " renderer(s) from the saved ones, " + s.Computed
                    + " worked out" + (s.Computed > 0 ? " (match: " + s.Stats.MatchSummary() + ", " + s.Stats.UvRangeSummary() + ")" : "") : ""));

            Texture bound = s.Material != null ? s.Material.GetTexture(Shader.PropertyToID("_BaseMap")) : null;
            Plugin.Log.LogInfo(Tag + "verify: material " + (s.Material != null ? "'" + s.Material.name + "' shader '" + s.Material.shader.name + "'" : "GONE")
                + " baseMap=" + (bound != null ? bound.name + " " + bound.width + "x" + bound.height : "NULL")
                + " | metallic " + F(s.Look.Metallic) + ", smoothness " + F(s.Look.Smoothness));

            for (int i = 0; i < s.Sample.Count; i++)
            {
                Renderer renderer = s.Sample[i];
                if (renderer == null)
                {
                    Plugin.Log.LogInfo(Tag + "verify: sample renderer " + (i + 1) + " is gone (merged or destroyed)");
                    continue;
                }

                Material[] now = renderer.sharedMaterials;
                int usingAtlas = 0;
                for (int m = 0; m < now.Length; m++)
                {
                    if (now[m] == s.Material)
                    {
                        usingAtlas++;
                    }
                }

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Plugin.Log.LogInfo(Tag + "verify: renderer " + (i + 1) + " enabled=" + renderer.enabled
                    + " active=" + renderer.gameObject.activeInHierarchy + " submeshes using the atlas material=" + usingAtlas + "/" + now.Length
                    + " | " + DiagnosticDump.MeshSummary(filter != null ? filter.sharedMesh : null));
            }
        }

        private static void Release(Session s)
        {
            if (s.Material != null)
            {
                AtlasMaterial.Release(s.Theme, s.Material);
            }

            s.Material = null;
            DestroyTexture(ref s.Look.Albedo);
            DestroyTexture(ref s.Look.Normal);
            DestroyTexture(ref s.Look.Occlusion);
            DestroyTexture(ref s.Look.Height);
            DestroyTexture(ref s.Look.MetalSmooth);
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }

            texture = null;
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
