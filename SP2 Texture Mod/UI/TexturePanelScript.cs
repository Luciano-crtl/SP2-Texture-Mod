using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts.UI;
using Assets.Scripts.UI.Controls;
using BepInEx.Configuration;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace SP2TextureMod.UI
{
    // The panel window. The game creates it from the layout in TexturePanel.BuildLayout()
    // and calls OnWidgetInitialized once its widgets exist.
    public class TexturePanelScript : PanelDialogScript
    {
        private const float RefreshInterval = 0.5f;
        private const int DetailRows = 16;

        private Widget content;
        private PanelBuilder builder;

        // Small functions that refresh the live rows (names and status).
        private readonly List<Action> refreshers = new List<Action>();

        private float nextRefresh;
        private bool rebuildRequested;

        // Doesn't block the game: you keep working on the craft while it is open.
        public override bool IsModal
        {
            get { return false; }
        }

        public override void OnWidgetInitialized(Widget widget)
        {
            base.OnWidgetInitialized(widget);

            widget.FindWidget("tex-close-button").Clicked += (Widget w) => Close();
            content = widget.FindWidget("tex-content");

            Plugin.Log.LogInfo("[UI] Textures panel created");
            TextureLibrary.Refresh();
            BuildContent();
        }

        // (Re)creates every row. Used on opening and after the lists or the settings were reset.
        private void BuildContent()
        {
            foreach (Widget child in content.Widgets.ToList())
                child.Destroy();
            refreshers.Clear();

            builder = new PanelBuilder(content);
            try
            {
                BuildFiles();
                BuildApply();
                BuildTiling();
                BuildMaps();
                BuildMaterial();
                BuildAlignment();
                BuildTools();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't build the Textures panel: " + e);
            }

            RefreshLive();
            nextRefresh = Time.unscaledTime + RefreshInterval;
        }

        // ---- Sections ------------------------------------------------------------

        private void BuildFiles()
        {
            builder.Header("Image and model");
            builder.Text("Mod version", Plugin.Version + " (phase " + Plugin.Phase + ")");
            builder.Text("Folder", TextureLibrary.Folder);

            Live("Image", () =>
            {
                TextureLibrary.Entry image = TextureLibrary.CurrentImage;
                return image == null
                    ? "none found: put PNG or JPG files in the folder"
                    : (TextureLibrary.ImageIndex + 1) + "/" + TextureLibrary.ImageCount + "  " + image.FileName;
            });
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Previous image", () => ChangeImage(-1)),
                new KeyValuePair<string, Action>("Next image", () => ChangeImage(1)),
            });

            Live("Model", () =>
            {
                string name = TextureLibrary.CurrentModelName;
                return name != null
                    ? name
                    : (TextureLibrary.ModelCount == 0 ? "none (no .obj in the folder): tiled" : "none: tiled");
            });
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Previous model", () => ChangeModel(-1)),
                new KeyValuePair<string, Action>("Next model", () => ChangeModel(1)),
            });

            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Refresh list", () => { TextureLibrary.Refresh(); rebuildRequested = true; }),
                new KeyValuePair<string, Action>("Open folder", TextureLibrary.OpenFolder),
            });
        }

        private void BuildApply()
        {
            builder.Header("Apply");
            builder.Text("Note", "Apply saves the texture for this craft (by its name). Opening the craft or flying it puts it on again. Remove takes it off and forgets it. Parts that move (swing wings, gear) must be in the model's pose when you press Apply; you can move them afterwards.");
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Apply to craft", AtlasApplier.ApplyCurrent),
                new KeyValuePair<string, Action>("Remove and forget", AtlasApplier.RemoveCurrent),
            });
            builder.Toggle("Put the saved texture on by itself",
                "With this on, opening a craft that has a saved texture (in the designer) or flying it puts the texture on again without pressing anything.",
                Plugin.AutoApply.Value,
                on =>
                {
                    SetValue(Plugin.AutoApply, on);
                    SaveConfig();
                });

            // The result of the last action, then the details of the last apply as short label/value rows.
            Live("Status", () => AtlasApplier.StatusText ?? "");
            for (int i = 0; i < DetailRows; i++)
            {
                int index = i;
                TextControl row = builder.Text("", "");
                refreshers.Add(() =>
                {
                    string[] pair = index < AtlasApplier.StatusRows.Count ? AtlasApplier.StatusRows[index] : null;
                    string label = pair != null ? pair[0] : "";
                    string value = pair != null ? pair[1] : "";
                    if (row.LabelText != label) row.LabelText = label;
                    if (row.ValueText.Text != value) row.ValueText.Text = value;
                });
            }
        }

        private void BuildTiling()
        {
            builder.Header("Tiling (image without a model)");
            FloatSetting(Plugin.TilesPerMeter, "Repetitions per meter",
                "How many times the image repeats per meter of the craft. Only used when no model is selected.",
                0.05f, 0.05f, 20f, "", AtlasApplier.ReapplyIfActive);
        }

        private void BuildMaps()
        {
            builder.Header("Maps (normal, occlusion, specular, height)", collapsed: true);
            builder.Text("Note", "The part shader reads a normal, an occlusion and a height map. They are found by name next to the albedo image (_n_n, _n_ao, _n_s, _n, _ao, _h or _bump) or chosen here. It has no specular map: only the average of that image is used.");
            MapRows(TextureLibrary.Normal);
            MapRows(TextureLibrary.Occlusion);
            MapRows(TextureLibrary.Specular);
            MapRows(TextureLibrary.Height);

            FloatSetting(Plugin.NormalStrength, "Normal strength", "How strong the relief of the normal map is.",
                0.05f, 0f, 3f, "x", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
            builder.Toggle("Flip normal green",
                "DirectX-style normal maps (many game textures) need the green channel flipped. Turn it on if the relief looks inverted (bumps look like dents).",
                Plugin.FlipNormalGreen.Value,
                on =>
                {
                    SetValue(Plugin.FlipNormalGreen, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });
            builder.Toggle("Rebuild normal blue",
                "Works the blue channel of the normal map out from red and green. Leave it on: many game textures keep other data in blue.",
                Plugin.RebuildNormalZ.Value,
                on =>
                {
                    SetValue(Plugin.RebuildNormalZ, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });
            builder.Toggle("Normal map from height map",
                "With no normal map selected, makes one from the height (bump) map. A grayscale image chosen as the normal map is converted the same way.",
                Plugin.HeightToNormal.Value,
                on =>
                {
                    SetValue(Plugin.HeightToNormal, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });
            FloatSetting(Plugin.BumpStrength, "Bump strength", "Strength used when a height (bump) image is converted to a normal map.",
                0.1f, 0.1f, 20f, "", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.OcclusionStrength, "Occlusion strength", "How dark the occlusion map makes the creases.",
                0.05f, 0f, 1f, "", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
            builder.Toggle("Parallax (height map)",
                "Uses the height map to give the surface depth. Experimental: it can distort the texture.",
                Plugin.UseParallax.Value,
                on =>
                {
                    SetValue(Plugin.UseParallax, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });
            FloatSetting(Plugin.ParallaxHeight, "Parallax depth", "Depth of the parallax effect.",
                0.005f, 0.005f, 0.08f, "", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
        }

        private void BuildMaterial()
        {
            builder.Header("Material (metallic, smoothness)", collapsed: true);
            builder.Text("Note", "One value for all of the textured parts: the part shader has no metallic, smoothness or specular map.");
            FloatSetting(Plugin.Metallic, "Metallic", "How metallic the textured parts look.",
                0.01f, 0f, 1f, "", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
            FloatSetting(Plugin.Smoothness, "Smoothness", "How glossy the textured parts look.",
                0.01f, 0f, 1f, "", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
            FloatSetting(Plugin.SpecularGain, "Specular map gain",
                "The average brightness of the specular map, times this, is added to the smoothness. 0 ignores the specular map.",
                0.1f, 0f, 6f, "x", AtlasApplier.SaveAssignment, AtlasApplier.RefreshMaterial);
        }

        private void MapRows(TextureLibrary.MapSlot slot)
        {
            Live(slot.Label, () => slot.CurrentName ?? "none");
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Previous", () => ChangeMap(slot, -1)),
                new KeyValuePair<string, Action>("Next", () => ChangeMap(slot, 1)),
            });
        }

        private static void ChangeMap(TextureLibrary.MapSlot slot, int delta)
        {
            TextureLibrary.StepMap(slot, delta);
            AtlasApplier.ReapplyIfActive();
        }

        private void BuildAlignment()
        {
            builder.Header("Model matching and alignment", collapsed: true);
            builder.Text("Note", "The model is aligned to the craft automatically. Adjust only if needed.");

            Live("Orientation", () => AtlasApplier.OrientationText);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Next orientation", AtlasApplier.NextOrientation),
                new KeyValuePair<string, Action>("Align again", () => { AtlasApplier.InvalidateFit(); AtlasApplier.ReapplyIfActive(); }),
            });

            builder.Toggle("Project along normals",
                "On: every point of the craft takes the texture from the first model surface met by a ray going inwards along its normal. Off: from the closest model surface.",
                Plugin.ProjectAlongNormals.Value,
                on =>
                {
                    SetValue(Plugin.ProjectAlongNormals, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });
            builder.Toggle("Mirror model on X",
                "Most importers mirror an OBJ on X when bringing it into Unity. Turn this off if the left and right sides of the texture come out swapped.",
                Plugin.FlipModelX.Value,
                on =>
                {
                    SetValue(Plugin.FlipModelX, on);
                    SaveConfig();
                    AtlasApplier.InvalidateFit();
                    AtlasApplier.ReapplyIfActive();
                });

            FloatSetting(Plugin.RayStart, "Ray start distance",
                "How far outside the craft's surface the rays start. It must be more than the craft sticks out of the model anywhere.",
                0.01f, 0.02f, 1.5f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.FacingWeight, "Facing preference",
                "Nearest-surface search and fallback: preference for model surfaces that face the same way. Keep it small.",
                0.005f, 0f, 0.3f, "", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustOffsetX, "Shift X", "Moves the model sideways after the automatic alignment.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustOffsetY, "Shift Y", "Moves the model up or down.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustOffsetZ, "Shift Z", "Moves the model forward or back.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationX, "Rotate X", "Extra rotation about X.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationY, "Rotate Y", "Extra rotation about Y.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationZ, "Rotate Z", "Extra rotation about Z.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustScale, "Scale", "Extra size of the model (1 = none).", 0.005f, 0.8f, 1.25f, "x", AtlasApplier.ReapplyIfActive);

            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Reset adjustments", ResetAdjustments),
            });
        }

        private void BuildTools()
        {
            builder.Header("Tools", collapsed: true);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Write diagnostic to log", DiagnosticDump.Dump),
                new KeyValuePair<string, Action>("Check shaders", ShaderProbe.Run),
            });
        }

        // ---- Actions -------------------------------------------------------------

        private static void ChangeImage(int delta)
        {
            TextureLibrary.StepImage(delta);
            AtlasApplier.ReapplyIfActive();
        }

        private static void ChangeModel(int delta)
        {
            TextureLibrary.StepModel(delta);
            AtlasApplier.InvalidateFit();
            AtlasApplier.ReapplyIfActive();
        }

        private void ResetAdjustments()
        {
            SetValue(Plugin.AdjustOffsetX, 0f);
            SetValue(Plugin.AdjustOffsetY, 0f);
            SetValue(Plugin.AdjustOffsetZ, 0f);
            SetValue(Plugin.AdjustRotationX, 0f);
            SetValue(Plugin.AdjustRotationY, 0f);
            SetValue(Plugin.AdjustRotationZ, 0f);
            SetValue(Plugin.AdjustScale, 1f);
            SaveConfig();
            AtlasApplier.ReapplyIfActive();
            rebuildRequested = true;   // the sliders show the old values until rebuilt
        }

        // ---- Row helpers ---------------------------------------------------------

        private void Live(string label, Func<string> getText)
        {
            TextControl row = builder.Text(label, getText());
            refreshers.Add(() =>
            {
                string text = getText();
                if (row.ValueText.Text != text) row.ValueText.Text = text;
            });
        }

        // afterRelease runs when the slider is let go; whileDragging runs on every step (for cheap changes).
        private void FloatSetting(ConfigEntry<float> entry, string label, string tooltip,
                                  float step, float min, float max, string unit, Action afterRelease, Action whileDragging = null)
        {
            int positions = Mathf.RoundToInt((max - min) / step) + 1;
            string format = NumberFormat(step);

            builder.Slider(label, tooltip, min, max, positions <= 1000 ? positions : 0,
                Mathf.Clamp(entry.Value, min, max),
                x => x.ToString(format, CultureInfo.InvariantCulture) + unit,
                x =>
                {
                    SetValue(entry, Mathf.Clamp(Mathf.Round(x / step) * step, min, max));
                    if (whileDragging != null) whileDragging();
                },
                x =>
                {
                    SaveConfig();
                    if (afterRelease != null) afterRelease();
                });
        }

        private static string NumberFormat(float step)
        {
            if (step >= 1f) return "0";
            int decimals = Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(step) - 0.001f), 1, 4);
            return "0." + new string('0', decimals);
        }

        // Changing a setting while dragging must not write the file on every step: saved on release.
        private static void SetValue<T>(ConfigEntry<T> entry, T value)
        {
            ConfigFile file = entry.ConfigFile;
            bool original = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            try
            {
                entry.Value = value;
            }
            finally
            {
                file.SaveOnConfigSet = original;
            }
        }

        private static void SaveConfig()
        {
            try
            {
                Plugin.Instance.Config.Save();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't save the settings: " + e.Message);
            }
        }

        // ---- Frame update --------------------------------------------------------

        protected void Update()
        {
            if (rebuildRequested)
            {
                rebuildRequested = false;
                BuildContent();
                return;
            }

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshInterval;
                RefreshLive();
            }
        }

        private void RefreshLive()
        {
            foreach (Action refresh in refreshers)
            {
                try
                {
                    refresh();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Panel refresh failed: " + e.Message);
                }
            }
        }

        protected void OnDestroy()
        {
            SaveConfig();
        }
    }
}
