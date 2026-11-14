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
| 1 | Plugin loads, logs version, `[COMPAT]` reflection check, `[DIAG]` dump (hotkey, default F9) | written, syntax-checked, **not compiled, not run** |
| 2 | Albedo path test: generated checkerboard + trivial per-part box-projection UV on every procedural part; must survive designer edits, flight, mesh combine | not started |
| 3 | Load atlas (PNG/JPG; TGA and DDS later) and OBJ; ghost preview and alignment; UV transfer with subdivision | not started |
| 4 | Juicy panel and settings; persistence per craft | not started |
| 5 | Multiplayer (default for now: local only) | not started |

Decisions taken without asking (change if the user disagrees): local-only textures; hotkey F9 for the dump; no textures bundled in the mod (War Thunder assets belong to Gaijin; each user loads their own files).

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
