using System;
using System.Collections.Generic;
using UnityEngine;

// Minimal stand-ins for the game's own types, with the signatures read from the decompiled game code.
// They exist only so that the mod's source can be compiled here.

namespace Assets.Scripts.Craft.Paint
{
    public enum PaintStyle
    {
        SolidColor,
        SinglePlaneTextureColorMask,
        TriPlaneTextureColorMask,
        AlbedoTexture,
        AlbedoTextureSupersampledWithMipmapBias
    }
}

namespace Assets.Scripts.Craft
{
    using Assets.Scripts.Craft.Paint;
    using Assets.Scripts.Craft.Parts;

    public enum CraftLoadContext
    {
        Default = 0,
        Menu = 1,
        Designer = 2,
        Flight = 4,
        Studio = 8
    }

    public class AircraftData
    {
        public string Name { get; set; }
    }

    public class PartMaterial
    {
        public int Id { get; set; }
        public PaintStyle Style { get; set; }
    }

    public class ThemeData
    {
        public int GetMaterialIndex(int materialId) { return 0; }
        public PartMaterial GetReservedMaterial(PaintStyle style) { return null; }
    }

    public class ThemeScript : MonoBehaviour
    {
        public Material Material { get; private set; }
        public ThemeData Theme { get; set; }
        public Material InitializeHighlightMaterial(Material material) { return null; }
        public Material InitializeHighlightSelectedMaterial(Material material) { return null; }
        public void ReleaseDefaultPartMaterialInstance(Material material) { }
        public Material RequestDefaultPartMaterialInstance() { return null; }
    }

    public class AircraftScript : MonoBehaviour
    {
        public AircraftData Aircraft { get; private set; }
        public CraftLoadContext LoadContext => Aircraft != null ? CraftLoadContext.Flight : CraftLoadContext.Designer;
        public List<PartData> Parts { get; private set; }
        public bool RemoteAircraft { get; private set; }
        public ThemeScript Theme { get; private set; }
    }

    public class BodyScript : MonoBehaviour
    {
        public AircraftScript Aircraft { get; set; }
        public List<PartGroupScript> PartGroups { get; private set; }
    }

    public class PartGroupScript : MonoBehaviour
    {
        public BodyScript Body { get; set; }
        public List<PartScript> Parts { get; private set; }
        private void Initialize() { }
    }
}

namespace Assets.Scripts.Craft.Parts
{
    public enum PartRendererMaterialType
    {
        DefaultShared = 0,
        DefaultInstanced = 1,
        CustomWithOriginalColors = 10,
        CustomWithThemeColors = 11
    }

    public class PartData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public PartScript PartScript { get; private set; }
    }

    public class PartScript : MonoBehaviour
    {
        public AircraftScript Aircraft { get; private set; }
        public PartData Part { get; private set; }
        public PartMaterialScript PartMaterialScript { get; set; }
    }

    public class PartMaterialScript : MonoBehaviour
    {
        public class PaintedEventArgs : EventArgs
        {
            public PaintedEventArgs(bool uvsChanged = false) { }
        }

        public class RendererMaterialMap
        {
            public bool ExcludeFromMeshCombine { get; set; }
            public Material[] HighlightMaterials { get; set; }
            public PartRendererMaterialType[] MaterialTypes { get; set; }
            public Mesh Mesh => null;
            public bool MeshIsUnique { get; set; }
            public Material[] OriginalMaterials { get; set; }
            public Renderer Renderer { get; set; }
            public Material[] SelectedMaterials { get; set; }
        }

        public List<RendererMaterialMap> RendererMaps { get; }
        public event EventHandler<PaintedEventArgs> OnPaintedInDesigner;
        public void ApplyAllMaterials() { }
        public void RefreshHighlight() { }
        public void RestoreOriginalMaterials() { }
    }
}

namespace Assets.Scripts.Craft.MeshGen
{
    public class ProceduralPartMeshRenderer : MonoBehaviour
    {
        public MeshRenderer MeshRenderer => null;
        public void UpdateMesh() { }
        public void UpdateMesh(int a) { }
    }
}

namespace Assets.Scripts.Design
{
    public class Designer : MonoBehaviour
    {
        public static Designer Instance { get; private set; }
        public Assets.Scripts.Craft.AircraftScript Aircraft { get; }
    }
}

namespace Jundroo.Common.Utils
{
    public static class UnityTransformUtility
    {
        public static Matrix4x4 GetTargetToAncestorTransformMatrix(Transform target, Transform ancestor) { return Matrix4x4.identity; }
    }
}
