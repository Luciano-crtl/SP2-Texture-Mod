# SP2 Texture Mod — notes for Claude

Read this first. The general method (phases, rules of conduct, game features catalog) is in `CREATE_SP2_MOD.md`, a guide the user attaches to the session; it is not stored in this repository.

## What this mod is

BepInEx 5 (Mono, x64) plugin for SimplePlanes 2, C# netstandard2.1, built by the user in Visual Studio. Claude can neither run the game nor compile: **everything below is untested until the user sends `BepInEx/LogOutput.log` or screenshots.**

**Goal:** make a texture atlas from a War Thunder model "fold" correctly onto a vehicle the user built in SP2 (example: a Ta 152 replica, 2000x2000 atlas with UV islands for wings, fuselage, tail...).

**Why the game's own custom textures fail** (user tried it, result smeared and wrong): `PaintTextures.xml` textures are *color masks* (up to 4 colors) projected flat or tri-planar from 3D position, not UV-mapped albedo. The procedural parts (fuselage, wings) have **no UV0** (all zeros), so a UV atlas cannot work on them without generating UVs.

**Planned approach ("UV transfer"):**
1. The user provides the atlas image and the matching War Thunder mesh (OBJ) and aligns the OBJ to the SP2 craft (ghost preview, position/rotation/scale sliders).
2. For each vertex of each part mesh, the mod finds the closest point on the War Thunder mesh (normal-aware) and copies its UV into UV0. Triangles are subdivided first, so interpolation does not smear across the gaps between atlas islands.
3. The parts get the reserved `AlbedoTexture` paint style and a material whose `mainTexture` is the atlas.
Decals (the game's own `CraftDecals.xml` system, full RGBA, projected) remain the right tool for national markings and small insignia.

## Status

| Phase | What | State |
|---|---|---|
| 1 | Plugin loads, logs version, `[COMPAT]` reflection check, `[DIAG]` dump (hotkey, default F9) | **done, verified in the game** (log received, 62 compat checks OK) |
| 2 | **done, verified in the game (designer)**: Albedo path test (hotkey toggles): generated checker + box-projection UV0 on every procedural part, UV1.x -> reserved Albedo style, private clone of the part material; re-applied after mesh rebuild and after paint; must also be tried in flight | written, syntax-checked, **not compiled, not run** |
| 3 | Load atlas (PNG/JPG; TGA and DDS later) and OBJ; ghost preview and alignment; UV transfer with subdivision | not started |
| 4 | Juicy panel and settings; persistence per craft | not started |
| 5 | Multiplayer (default for now: local only) | not started |

Decisions taken without asking (change if the user disagrees): local-only textures; hotkey F9 for the dump; no textures bundled in the mod (War Thunder assets belong to Gaijin; each user loads their own files).

## Phase 1 results (verified in the game, user's log)

Game 0.1.100, Unity 6000.3.20, BepInEx 5.4.23.5. User's test craft: 93 parts, 145 renderers, 64645 vertices.
- Shader of parts: `Game/Parts/Part - Default` (and `Game/Parts/Part - Transparent` for windows). Properties: `_SinglePlaneTextureColorMask`, `_TriPlaneTextureColorMask`, `_UseBakedPositionsAndNormals`, **`_BaseMap`** (albedo; no `_MainTex`, no `_BaseColor`), `_SupersampleMipmapBias`, `_BumpMap`, `_BumpScale`, `_OcclusionMap`, `_OcclusionStrength`, `_ParallaxMap`, `_Parallax`, `_EmissionColor`, `_EmissiveOverride`, `_EmissiveOverrideNight`, `_Alpha`, `_WaterHeight`, `_UseSimpleLightingEnabled`, `_RECEIVE_SHADOWS`. No keywords enabled on the shared material.
- Reserved materials: `AlbedoTexture` id=-5 shaderIndex=24 styleValue=3; `AlbedoTextureSupersampledWithMipmapBias` id=-6 shaderIndex=25 styleValue=4 (theme "Custom" had 26 materials).
- UV0 of procedural meshes (Fuselage, Glass Fuselage, Nosecone, Wing, Flap, Window): **all zero**, confirmed. Premade parts (gun, rotator, wheel, propeller cap...) have real UV0 but use shared meshes (`uniqueMesh=False`): never modify those.
- Procedural meshes are readable and unique (`uniqueMesh=True`). Typical size 250-1000 vertices; this craft has 51 fuselage parts, 6 wings, 11 flaps, 5 windows.
- `Wing`, `Flap`, `Window` and `Propeller Assembly` have `combine=False` (never merged in flight); `Fuselage` has `combine=True`. 22 renderers already `excludeCombine=True` (flaps, wheels).
- Another BepInEx plugin was loaded: "SP2 Reference Model 0.7.0" (injects controls into the designer main menu). Ask the user whether it is theirs: it may already hold the reference model transform that Phase 3 needs.

## Phase 3c second run (verified): it works; Phase 3d = PBR maps

The user's screenshot after the V fix: green and grey camouflage, crosses on the wings, serial numbers on the fuselage, panel lines. The alignment took 3.7 s, "Orientation 1/3 (mean distance 0.034 m)". The model's faces agree 64% at the alignment samples (thin parts make that vote noisy; the nearest-surface runs agreed 90-96%).

**Phase 3d (written, not compiled, not run): maps.** What the part shader `Game/Parts/Part - Default` accepts (its property list from the Phase 1 log): `_BaseMap`, `_BumpMap`+`_BumpScale` (keyword `_NORMALMAP`), `_OcclusionMap`+`_OcclusionStrength` (`_OCCLUSIONMAP`), `_ParallaxMap`+`_Parallax` (`_PARALLAXMAP`), `_EmissionColor`, `_EmissiveOverride(Night)`. There is NO metallic, smoothness, specular or emission map: metallic, smoothness and emission day/night come per paint material from `_MaterialData` (entry layout in the Phase 2 notes: [3] = metallic, smoothness, emissionDay, emissionNight of color 0). The game itself uses exactly these three maps for premade textured parts (`ThemeScript.RequestTexturedPartMaterial`), and when a group has textured materials the combined mesh gets a tangent channel, so the shader reads tangents.
- `TextureLibrary.MapSlot` (Normal, Occlusion, Height): chosen among the folder's images or "none"; found by name next to the albedo image (`fw_190d_9_a` -> `fw_190d_9_n`, `_ao`, `_h`/`_bump`...; endings in `Suffixes`), saved as `Maps/SelectedNormal|SelectedOcclusion|SelectedHeight` (`auto`, `none` or a file name); re-paired whenever the albedo image changes.
- `MapTextures`: maps are decoded into raw pixels and rebuilt as LINEAR textures (no sRGB). Normal: alpha forced to 255 (the shader's unpacking multiplies by it), optional green flip (`FlipNormalGreen`, off by default; DirectX-style maps need it), optional blue rebuilt from red/green (`RebuildNormalZ`, on; many game textures keep other data in blue); a grayscale image chosen as normal map is taken as a bump map and converted (central differences, `BumpStrength`); `HeightToNormal` makes a normal map from the height slot when no normal map is chosen. Occlusion: gray used as is, otherwise the red channel, copied to all channels (shader reads G). Height: made gray (shader reads G); parallax is opt-in (`UseParallax`, off).
- `AtlasApplier.ConfigureMaterial` sets texture + keyword + strength on the atlas material (`_NORMALMAP`/`_OCCLUSIONMAP`/`_PARALLAXMAP` via `EnableKeyword`, as the game does); `RefreshMaterial` (used by the sliders while dragging) updates it in place. A normal or height map makes `ApplyRenderer` call `mesh.RecalculateTangents()` (needs the normals and the UV0 just written) and remember it (`RendererState.WroteTangents`); revert sets `mesh.tangents = null`.
- Metallic/Smoothness (`Material/Metallic`, `Material/Smoothness`): one value for all textured parts, written into the atlas material's own copy of `_MaterialData` (entry of the reserved albedo style, element 3), not into the craft's theme (so nothing is saved in the craft). The theme rewrites `_MaterialData` of its materials on every paint change, so `ThemeMaterialPatch` (postfix on the private `ThemeScript.UpdateMaterialProperties(Material)`) puts the values back.
- UNVERIFIED, check in the log/screens: (1) the `[TEX] verify: maps | normal True (_NORMALMAP True)` line; (2) whether the build keeps the shader variants for each keyword combination (the game uses `_NORMALMAP`/`_OCCLUSIONMAP`/`_PARALLAXMAP`, but maybe not every combination; a missing variant may render the plain one); the dump (Tools > Write diagnostic) now lists all the shader's keywords; (3) that `RecalculateTangents` works on these custom-layout meshes and that the game's later mesh reads (decals, `FuselageCutter.CheckMeshFormat`) are fine with the extra channel; (4) that metallic/smoothness in `_MaterialData` really drives the lighting of the albedo style; (5) the normal-map convention of the user's War Thunder maps (green flip).
- Panel: sections "Maps (normal, occlusion, height)" and "Material (metallic, smoothness)" (collapsed); sliders for strengths/metallic/smoothness update live (`whileDragging`), map choices and conversions re-apply.

## Phase 3c first run (verified in the game): the real cause was the V coordinate

The panel and the menu button worked (the Reference Model's technique: clone one of the game's own menu buttons). The texture was still wrong, and the log showed why: the OBJ's `vt` have **negative v** (`uv0=[(0.02, -0.906) .. (1.116, -0.007)]`; many exporters write v - 1, some islands also exceed u = 1) and the atlas was set to `Clamp`, so every fragment with v < 0 sampled the single bottom row of the image. Measured on the user's atlas: its bottom row is 90% light, 8% dark, 0% green = "almost white with a few black marks", exactly what was seen. The earlier hypotheses (wrong skin on thin wings, upside-down model, inside-out faces) were wrong: the numbers in the same log disprove them (`faces up: match -0.02 m high`, fit 0.033 m, nearest-surface agreement 90-96%). The checker's colors were never washed out either (a crop of the old render shows normal mid-tone pastel; only my wrong "dark grid lines look white" reading suggested it).
Fix (Phase 3c, second build, not run yet): `ObjModel` shifts all texture coordinates by the whole number that puts the median of u and v inside [0, 1) (v - 1 -> v) and logs `[OBJ] ... texture coordinates u a..b, v c..d`; the atlas always uses `Repeat` (tiling needs it too); the debug map draws wrapped coordinates.
Other changes: the alignment takes ~3 s instead of ~11 s (400 samples, 3 refined candidates, 8 rounds); the panel shows the result as short label/value rows (the long single text overlapped the next rows); `Phase` is "3c".
Lesson for later fixes: when a mapping looks like a smear of one color, read the UV range in the log first.

## Phase 3 second run (OBJ used) and Phase 3c

Screenshot + overlay of the phase 3 build with the OBJ paired: `Alignment: orientation #0, scale 0.965, mean distance 0.2 m at the first guess, 0.041 m after refining (0.39% of the craft)`; transfer stats `vertices=29752 meanDistance=0.139 m maxDistance=0.588 m`. So the geometric fit is excellent, but the texture was wrong: the plane was almost white with a few black marks; the wing tops showed the black cross fragments of the UNDERSIDE island of the atlas (the atlas has the camo wing tops as two tall islands left/right, the light-grey undersides in the center column, the fuselage sides in the long island at the bottom). Cause: per-vertex matching picked the wrong skin of the thin parts. Contributing facts found by reasoning:
- The facing term was far too strong (default 0.25 m2 = a 0.5 m distance penalty at opposite normals; mean match distance rose from 0.04 to 0.139 m): a surface 0.1-0.2 m away with a better-aligned normal beat the right skin.
- The model could also have the other orientation (a thin, symmetric aircraft fits upside-down almost as well as upright when the worst 15% of the samples are trimmed away, and the trimmed samples were exactly the canopy and belly that tell them apart); or inside-out faces (winding), which the old code assumed correct.
- The user's guess that "the vertices of the SP2 model are located very differently from the War Thunder model" is right about topology (procedural rings vs a hand-made mesh, so nothing can be matched by index) and is why the match is by position; position itself is fine.

**Phase 3c (written, not compiled, not run):**
- Matching by projection (default, setting `ProjectAlongNormals`): a ray starts `RayStart` (0.3 m) outside each craft vertex and goes inwards along its normal; the first model face it meets gives the UV (`TriangleBvh.Raycast`, Moller-Trumbore + slab test). It cannot land on the other skin of a thin wing or on interior parts, and with `FrontFacesOnly` off it ignores the model's winding. The nearest-surface search remains as the fallback (and as an option) with a small `FacingWeight` (0.03 m2; the old `NormalWeight` setting was dropped).
- Orientation: `ModelAlignment.FitCandidates` keeps the 6 best orientations after refinement, sorted by mean distance (only the worst 2% trimmed, 10% bonus for orientations that keep up = up); the panel has "Next orientation" (`AtlasApplier.NextOrientation`) and shows `k/N (mean distance)`.
- Winding check: at the sample points the model's face normal is compared with the craft's own normal; below 50% agreement the model's normals are turned around (`TriangleBvh.SetNormalsInverted`); front-face-only rays are used only when agreement is at least 75%.
- Diagnostics: stats per kind of vertex (faces up / down / sideways: count, how often the matched face agrees, mean height of the match above the vertex) in the log and the panel; `UvDebugMap` writes `debug_uv_map.png` in the texture folder (the atlas darkened with one dot per craft vertex: red = faces up, blue = faces down, green = sideways, yellow = fallback) so a wrong-island match is visible at a glance; files starting with `debug_` are ignored by the image list.
- The designer menu button is now a clone of one of the game's own menu buttons (found by its label "Designer Tutorials" / "Undo History" / "Upload Craft"), exactly as the open-source SP2 Reference Model 0.7.0 mod does (its source was provided by the user: the mod is NOT the user's; it uses plain uGUI Button + TextMeshPro, not Juicy, `Object.Instantiate` of the template button, `onClick.RemoveAllListeners()` then its own listener, polling every 0.5 s while `Designer.Instance != null`). The Juicy widget route (phase 3b) did not work for the user (button/panel not working; no log was received), so the button no longer depends on Harmony timing or Juicy widget parents. The panel itself is still the Juicy dialog (`CreateDialog<TexturePanelScript>`); `[UI]` log lines say whether it was created.
- About the Reference Model mod (useful later): it loads OBJ with X mirrored and fan `(0, i+1, i)` winding, like ours; it keeps the model's transform per file in `BepInEx/config/SP2ReferenceModel/State`; it can drive the designer's move/rotate gizmos on an external transform (`TransformTool.SetExternalTarget`), a way to align a ghost model by hand if the automatic alignment ever fails.

## Phase 3 first run (verified in the game) and Phase 3b

The user's first screenshot of Phase 3 showed `(tiled: 0.11 per meter)` and no `+ model`: no `.obj` with the same file name as the image was in the folder, so the mod correctly fell back to tiling the atlas. Lesson: image and model must be chosen separately, and the UI must say which model is used.

**Phase 3b (written, not compiled, not run):** the keys are gone except one optional `PanelKey` (Insert); the interface is a panel built with the game's own UI library (Juicy), like the author's Armor Mod does for the flight menu (`UI/` folder; `PanelBuilder` is adapted from `Armor Mod/UI/PanelBuilder.cs`, the user's own code and the user asked for the same approach).
- A "Textures" button is added to the designer's main menu: Harmony postfix on `DesignerPanelScript.InitializeDesignerPanel` filtered to `FileMenuPanelScript` (the menu does not override it); the button goes right after the first widget found whose click handler is `OnExportClicked`/`OnEnvironmentClicked`/... (the handlers are wired by name from the game's XML, `Widget.EventClick`), copying that widget's `class`. The log line `[UI] Textures button added ...` says where it went.
- The panel (`TexturePanelScript : PanelDialogScript`, non-modal) is handed to the game by the same three patches as in the Armor Mod: `Jundroo.Juicy.ResourceLoader.LoadXml` prefix (our XML for the path `Mods/SP2TextureMod/Panel`), `GetScriptType` postfix (types starting with `SP2TextureMod.`), and the button. Sections: Image and model (previous/next image, previous/next model incl. "none", refresh list, open folder), Apply (Apply to craft / Remove + 4 status lines), Tiling slider, Model alignment (mirror toggle, shift, rotate, scale, facing preference, Align again, Reset adjustments), Tools (diagnostic dump).
- `TextureLibrary` now keeps images and models in separate lists; a same-named pair is selected automatically; the choice is saved in the settings (`SelectedTexture`, `SelectedModel`: file name, `auto` or `none`).
- The automatic alignment is cached per aircraft/model/mirror setting, so moving an adjustment slider only re-applies.
- Verified from the decompile: `Aircraft.Aircraft.Name` is the craft's name (useful for a per-craft record later); `DesignerFlyouts.RegisterFlyout` calls `InitializeDesignerPanel` after the widget tree exists.

## Phase 2 second run (verified in the game)

With the supersampled style (4) the generated checker is drawn correctly on every procedural part of the Athena (fuselage, wings, tail). So the whole path works in the designer: UV0 write, UV1.x -> reserved style, private material clone with `_BaseMap`. **Use style 4 (`AlbedoTextureSupersampledWithMipmapBias`) with `_SupersampleMipmapBias = -1`; the plain style 3 is not drawn.**
Also from the user's log: the game's own key table has F9 = ScreenshotMode, F10 = SelfDestruct (in flight!), F11 = DamageVisualizer; never use those. Keys now: Insert apply/remove, PageUp/PageDown image, Home/End tiling, Pause = log dump.

## Phase 3 (written, not compiled, not run): own images and War Thunder model UV transfer

- `TextureLibrary`: folder `<persistentDataPath>/TextureMod` (created with a README); PNG/JPG; an image with an OBJ of the same name uses UV transfer, an image alone is tiled with a box projection. Selection kept in the setting `Texture/SelectedTexture`.
- `ObjModel` (OBJ parser, fan triangulation, optional X mirror), `TriangleBvh` (nearest point with a normal-aware cost), `ModelAlignment` (best of 24 axis orientations + coordinate descent on position, scale, rotation; quality = trimmed mean distance craft samples -> model surface), `UvTransfer` (per vertex: UV of the closest model point), `AtlasApplier` (orchestration, reversible, re-applied after mesh rebuild and paint).
- Manual correction on top of the automatic fit: settings `Model adjustment/*` (offset, rotation, scale), edited with sliders in the panel (Phase 3b).
- The result is written to the log as `[TEX] ...` lines: model size, craft size, alignment summary, match quality (mean/max distance, % of vertices farther than `MaxMatchDistance`).
- Known limit of v1: per-vertex lookup, no subdivision; triangles that straddle two UV islands of the atlas will show a thin smeared band along the seam. Next step if it matters: subdivide only the triangles whose vertices fall in different islands. Designer only: not saved in the craft file, not applied in flight (needs the apply step before the mesh merge and a per-craft record).

## Phase 2 first run (verified in the game, user's log and screenshots)

Mechanics all worked, no exceptions: 144-281 procedural renderers per craft were switched in 10-70 ms, every submesh used the atlas material, `_BaseMap` was the checker (512x512), UV0 was filled (box projection) and UV1.x = 24 everywhere, `_MaterialData[24]` had header.x = 3 (plain Albedo style) with white color, revert restored the craft exactly, decals stayed visible on top. **But the plane rendered plain white with correct shading: the shader did not draw the texture in the plain `AlbedoTexture` style (3).**
Hypothesis: style 3 is a reserved placeholder the shader does not implement; the only style the game itself uses with a texture is style 4 (`AlbedoTextureSupersampledWithMipmapBias`, gauge faces). The next build cycles F10: 1st press style 4 (+ `_SupersampleMipmapBias` = -1), 2nd press style 3, 3rd press revert, to settle it in one session.
Layout of `_MaterialData` per material (10 Vector4): [0] header (style, textureIndex, blend z, normalization flags), [1] wrap modes (+ color-count flag in w), [2] color 0 (linear), [3] metallic/smoothness/emissionDay/emissionNight of color 0, then the same pair for colors 1-3.
Also seen: two copies of the plugin DLL were in `BepInEx/plugins` ("Skipping [SP2 Texture Mod 0.1.0] because a newer version exists"): same version, so it is arbitrary which one loads. The user must keep one. The user's crafts carry `[ARM t=.. h=.. b=..]` tags in part names (from another mod); this mod never touches part names.

## Phase 2: how to test

1. Designer, craft open, press F10. Expect `[TEST] applied to N renderer(s) of M part(s)` and a colored checker on fuselage and wings (red grows along one texture axis, green along the other). Windows (other shader) and premade parts are skipped on purpose.
2. Hover and select parts: the texture must stay. Paint a part with another color: it must stay (handled through `OnPaintedInDesigner`). Resize or edit a fuselage part: the texture must follow (handled by the `UpdateMesh` postfix).
3. F10 again reverts everything (`[TEST] reverted`). The craft file is never modified (MaterialIds untouched).
4. Flight: F10 in flight is only partly representative (meshes are merged at load); wings should show it, fuselage probably not. The real flight path needs the apply step before the merge (Phase 4).
5. F10 cycles: 1st press supersampled Albedo style, 2nd plain Albedo style, 3rd revert. Say which of the two shows the checker.

Not saved anywhere: undo/redo and symmetry recreate parts, which lose the texture until F10 is pressed twice.

## Phase 1: how to test

1. Copy `SP2 Texture Mod/GameDir.props.example` to `GameDir.props` and set `GameDir`. Build in Visual Studio; the DLL is copied to `<game>\BepInEx\plugins\` when the folder exists.
2. Start the game. Expected in `BepInEx/LogOutput.log`: `SP2 Texture Mod v0.1.0 loaded ...`, then `[COMPAT] N checks, all OK` (or one `[COMPAT]` line per missing member), then `[PATCH] 0 applied, 0 skipped`.
3. Open a craft (designer or flight), press F9. Expected: a `[DIAG]` block with the theme, the reserved Albedo material ids, every distinct shader with its properties and keywords, parts/renderers, and a `Summary:` line.
4. Send the whole log back. The questions it answers: are the procedural meshes' UV0 all zero (`uv0: allZero=...`), what are the shader property names (`_BaseMap` vs `_MainTex`), what are the reserved Albedo ids, which renderers are excluded from combining.

## Findings from the decompiled game code (read, not run)

Paths are relative to the decompile root (`game/Assets.Scripts.*`). The decompiles are the user's study material and are never committed (`_decompiled/` is git-ignored).

**Paint system** (`Assets.Scripts.Craft.Paint`, `Craft/ThemeScript.cs`, `Craft/ThemeData.cs`, `Craft/PartMaterial.cs`)
- `PaintStyle`: SolidColor, SinglePlaneTextureColorMask, TriPlaneTextureColorMask, and two styles hidden from the UI: `AlbedoTexture`, `AlbedoTextureSupersampledWithMipmapBias`.
- The part shader does not use one material per part. `ThemeScript.UpdateMaterialData` packs every theme material into `_MaterialData` (Vector4[560], 10 per material, max 56) plus `_MaterialTextureMatrixData`; the mesh stores which material to use per vertex in **UV1 = (materialIndex, decalLayer, partId)**.
- `ThemeData` creates the reserved Albedo materials in code with negative ids (`GetMaterialIndex`: negative id -> `Materials.Count - 1 - id`). `PartMaterialScript.SetMaterialNoEvents` keeps a negative `Part.MaterialIds[level]` untouched, so setting it to the Albedo id survives repaints and is saved with the craft.
- `PaintTextureManager`: user paint textures come from `<persistentDataPath>/PaintTextures/PaintTextures.xml`; they are mask textures, converted to BC7 DDS cache on Windows.

**The one working example of a full albedo texture on a mesh** (`Craft.Parts.Modifiers/GaugeScript.cs`, `Initialize` and `OnGaugeFaceChanged`):
`PartMaterialScript.ApplyReservedPaintStyle(PaintStyle.AlbedoTextureSupersampledWithMipmapBias, mesh)` (writes UV1 for the whole mesh), then `aircraft.Theme.RequestDefaultPartMaterialInstance()` (a clone of the shared part material that stays registered for `_MaterialData` updates), `SetFloat("_SupersampleMipmapBias", -1f)`, `renderer.material = clone`, and `clone.mainTexture = texture`. The gauge face mesh has real UV0.

**Procedural meshes** (`Craft.MeshGen/ProceduralPartMeshRenderer.cs`, `PartMeshVertexUVs.cs`, `ManifoldUtils.cs`, `NativeMesh.cs`)
- Fuselage, wings, control surfaces and carver parts use `ProceduralPartMeshRenderer`; `UpdateMesh(NativeMesh, bool)` and `UpdateMesh(Manifold<Vertex>, bool)` rebuild the mesh on every edit. Vertex layout: stream 0 = Position + Normal, stream 1 = **UV0 (float2, always default = 0)** + UV1 (float3). So the UV0 slot exists but is never filled: write it after each `UpdateMesh` (Harmony postfix on both overloads; private fields `_partScript`, `_mesh`, `_renderer`).
- `PostUpdateMesh` calls `AddRenderer` (new `RendererMaterialMap`) when the submesh layout changes, so materials set earlier can be reset: re-apply after it.

**Mesh combining in flight** (`Craft/PartGroupScript.cs`: `SurveyPartMeshes`, `CombineMeshes`, `GetTexturedMaterial`)
- Renderers are merged per group; the combined mesh keeps UV0. Only submeshes whose `MaterialTypes[i] == DefaultInstanced` **and** whose material is registered in `ThemeScript._texturedPartMaterialReferences` (via `RequestTexturedPartMaterial(normal, occlusion, parallax)`) keep their own material, up to 8 per group; everything else is rendered with the shared part material, which would lose the atlas.
- Options for Phase 2: (a) set `RendererMaterialMap.ExcludeFromMeshCombine = true` on textured renderers (simple, more draw calls); (b) obtain the material through `RequestTexturedPartMaterial` with a private flat-normal dummy texture as key, set the atlas on it, and mark the submeshes `DefaultInstanced` (combinable, one material for the whole craft). Decide with the Phase 2 result.

**Material application** (`Craft.Parts/PartMaterialScript.cs`)
- `UpdateMaterial()` runs on selection, hide, damage and so on and ends with `RestoreOriginalMaterials()`, which assigns `RendererMaterialMap.OriginalMaterials`. So the atlas material must be stored in `OriginalMaterials[i]`, not only on `renderer.material`.
- `OnThemeUpdated` -> `ApplyAllMaterials` rewrites UV1.x for default-type renderers.

**Decals** (`Craft.Decals/*`, `Craft.Parts.Modifiers/TextureDecalData.cs`): user decals come from `<persistentDataPath>/CraftDecals/CraftDecals.xml` + PNG/JPG (`GameData.LoadTexture` = `Texture2D.LoadImage`, so no DDS/TGA); each decal has id, displayName, category, path, aspectRatio. `DesignerPropertyTexture` + `ITexturePropertyHandler` give a texture picker in the part properties for free.

**Hooks that exist** (guide section 4): `Designer.Instance.Aircraft`, `Designer.SelectedPart`, `Designer.CreateUndoStepForSelectedPart`, `PartPropertiesPanelScript` (`InitializeDesignerPanel`, private `SetSelectedPart`, private `UpdateCommonPartProperties`), `PartModifierData.OnPartNameChanged`, `INetworkAircraft.SendPartNetworkMessage`, `FlightSceneScript.Instance.LocalPlayer.Aircraft`. `AircraftScript.Parts` is a `List<PartData>`; the script is `PartData.PartScript`.
- `PanelBuilder` (guide 4.3) is not in the decompiles; it belongs to the armor mod. Use the game's property system or Juicy XML directly.
- Not in any decompile: the shader source (Unity asset). Property names come from the Phase 1 dump.

## Known risks

- Quality of UV transfer on simplified parts (wing thickness, fuselage cross-section differ from the War Thunder mesh); subdivision limits streaks but cannot fix mismatched geometry.
- Vertex budget: combined meshes stop at 65000 vertices per group (`SurveyPartMeshes`), subdivided parts may fall out of combining.
- Anything that rebuilds a mesh or the renderer maps (designer edits, symmetry, undo) must re-trigger the UV and material step.
- Craft files saved with the Albedo style open in vanilla as a plain untextured material (harmless).

## Tools

`tools/check_syntax.py` (tree-sitter) syntax-checks the C# without compiling; setup and usage in its docstring. It does not catch type errors or wrong game member names: re-read the code against the decompiles before delivering.
