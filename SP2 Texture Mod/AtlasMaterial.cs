using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Paint;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// What a textured material shows: the images (owned by whoever made them) and the constant values
    /// that stand in for the maps the part shader does not have (metallic, smoothness).
    /// </summary>
    internal sealed class AtlasLook
    {
        public Texture2D Albedo;
        public Texture2D Normal;
        public Texture2D Occlusion;
        public Texture2D Height;
        public float NormalStrength = 1f;
        public float OcclusionStrength = 1f;
        public float ParallaxHeight = 0.02f;
        public float Metallic;
        public float Smoothness;
    }

    /// <summary>
    /// Makes and keeps up the material that carries the user's texture. It is the same for the designer and for
    /// flight: a clone of the theme's part material (so the theme keeps its _MaterialData up to date) whose
    /// _BaseMap is the image, with the shader's own switches for the normal, occlusion and height maps.
    /// </summary>
    internal static class AtlasMaterial
    {
        // The textured style the game itself uses (gauge faces). The plain AlbedoTexture style is not drawn by the shader.
        public const PaintStyle Style = PaintStyle.AlbedoTextureSupersampledWithMipmapBias;

        private sealed class Made
        {
            public ThemeScript Theme;
            public AtlasLook Look;
        }

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int SupersampleBiasId = Shader.PropertyToID("_SupersampleMipmapBias");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");
        private static readonly int ParallaxMapId = Shader.PropertyToID("_ParallaxMap");
        private static readonly int ParallaxId = Shader.PropertyToID("_Parallax");
        private static readonly int MaterialDataId = Shader.PropertyToID("_MaterialData");

        // Every material made here and the look it must keep (the theme rewrites _MaterialData now and then).
        private static readonly Dictionary<Material, Made> Materials = new Dictionary<Material, Made>();

        /// <summary>A new material with the look, or null if the theme cannot give an instance.</summary>
        public static Material Create(ThemeScript theme, AtlasLook look)
        {
            Material material = theme.RequestDefaultPartMaterialInstance();
            if (material == null)
            {
                return null;
            }

            material.name = "SP2TextureMod Atlas";
            material.SetFloat(SupersampleBiasId, -1f);
            Materials[material] = new Made { Theme = theme, Look = look };
            Update(material, theme, look);
            return material;
        }

        /// <summary>Puts the look (images, strengths, metallic, smoothness) on a material made here.</summary>
        public static void Update(Material material, ThemeScript theme, AtlasLook look)
        {
            material.SetTexture(BaseMapId, look.Albedo);

            SetMap(material, "_NORMALMAP", BumpMapId, look.Normal);
            if (look.Normal != null)
            {
                material.SetFloat(BumpScaleId, look.NormalStrength);
            }

            SetMap(material, "_OCCLUSIONMAP", OcclusionMapId, look.Occlusion);
            if (look.Occlusion != null)
            {
                material.SetFloat(OcclusionStrengthId, look.OcclusionStrength);
            }

            SetMap(material, "_PARALLAXMAP", ParallaxMapId, look.Height);
            if (look.Height != null)
            {
                material.SetFloat(ParallaxId, look.ParallaxHeight);
            }

            ApplyConstants(material, theme, look);

            Made made;
            if (Materials.TryGetValue(material, out made))
            {
                made.Theme = theme;
                made.Look = look;
            }
        }

        /// <summary>Gives the material back to its theme (which also frees its highlight copies).</summary>
        public static void Release(ThemeScript theme, Material material)
        {
            if (material == null)
            {
                return;
            }

            Materials.Remove(material);
            if (theme != null)
            {
                theme.ReleaseDefaultPartMaterialInstance(material);
            }
            else
            {
                UnityEngine.Object.Destroy(material);
            }
        }

        /// <summary>
        /// The theme rewrites _MaterialData of every material it keeps whenever the paint changes; this puts the
        /// metallic and smoothness back on ours.
        /// </summary>
        public static void OnThemeMaterialUpdated(ThemeScript theme, Material material)
        {
            if (Materials.Count == 0 || material == null)
            {
                return;
            }

            Made made;
            if (Materials.TryGetValue(material, out made))
            {
                ApplyConstants(material, theme, made.Look);
            }
        }

        /// <summary>Forgets the materials that were destroyed with their aircraft.</summary>
        public static void Prune()
        {
            if (Materials.Count == 0)
            {
                return;
            }

            List<Material> dead = new List<Material>();
            foreach (KeyValuePair<Material, Made> pair in Materials)
            {
                if (pair.Key == null)
                {
                    dead.Add(pair.Key);
                }
            }

            for (int i = 0; i < dead.Count; i++)
            {
                Materials.Remove(dead[i]);
            }
        }

        /// <summary>Index of the reserved albedo paint style among the theme's materials (what UV1.x must hold).</summary>
        public static int AlbedoShaderIndex(ThemeScript theme)
        {
            PartMaterial reserved = theme.Theme.GetReservedMaterial(Style);
            return theme.Theme.GetMaterialIndex(reserved.Id);
        }

        /// <summary>
        /// The part shader has no specular map. The average of the specular image is added to the smoothness,
        /// which is a single value for the whole material.
        /// </summary>
        public static float EffectiveSmoothness(float smoothness, float specularAverage, float gain)
        {
            return Mathf.Clamp01(smoothness + Mathf.Max(0f, specularAverage) * Mathf.Max(0f, gain));
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
        // material instance gets the values; the craft's own theme is not touched.
        private static void ApplyConstants(Material material, ThemeScript theme, AtlasLook look)
        {
            int index = AlbedoShaderIndex(theme) * 10 + 3;
            Vector4[] data = material.GetVectorArray(MaterialDataId);
            if (data == null || data.Length <= index)
            {
                return;
            }

            Vector4 values = data[index];
            values.x = look.Metallic;
            values.y = look.Smoothness;
            data[index] = values;
            material.SetVectorArray(MaterialDataId, data);
        }
    }
}
