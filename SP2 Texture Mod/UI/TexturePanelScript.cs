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
        private const int StatusRows = 4;

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
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Apply to craft", AtlasApplier.ApplyCurrent),
                new KeyValuePair<string, Action>("Remove", AtlasApplier.RemoveCurrent),
            });

            // The result of the last apply, one line per row (the text has line breaks).
            for (int i = 0; i < StatusRows; i++)
            {
                int line = i;
                Live(i == 0 ? "Status" : "", () =>
                {
                    string[] lines = (AtlasApplier.StatusText ?? "").Split('\n');
                    return line < lines.Length ? lines[line] : "";
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

        private void BuildAlignment()
        {
            builder.Header("Model alignment", collapsed: true);
            builder.Text("Note", "The model is aligned to the craft automatically. Adjust only if needed.");
            builder.Toggle("Mirror model on X",
                "Most importers mirror an OBJ on X when bringing it into Unity. Turn this off if the left and right sides of the texture come out swapped.",
                Plugin.FlipModelX.Value,
                on =>
                {
                    SetValue(Plugin.FlipModelX, on);
                    SaveConfig();
                    AtlasApplier.ReapplyIfActive();
                });

            FloatSetting(Plugin.AdjustOffsetX, "Shift X", "Moves the model sideways after the automatic alignment.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustOffsetY, "Shift Y", "Moves the model up or down.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustOffsetZ, "Shift Z", "Moves the model forward or back.", 0.01f, -3f, 3f, " m", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationX, "Rotate X", "Extra rotation about X.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationY, "Rotate Y", "Extra rotation about Y.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustRotationZ, "Rotate Z", "Extra rotation about Z.", 0.1f, -30f, 30f, " deg", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.AdjustScale, "Scale", "Extra size of the model (1 = none).", 0.005f, 0.8f, 1.25f, "x", AtlasApplier.ReapplyIfActive);
            FloatSetting(Plugin.NormalWeight, "Facing preference",
                "How strongly a point of the craft prefers model surfaces that face the same way. Raise it if the top of a wing picks the bottom of the model.",
                0.01f, 0f, 2f, "", AtlasApplier.ReapplyIfActive);

            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Align again", () => { AtlasApplier.InvalidateFit(); AtlasApplier.ReapplyIfActive(); }),
                new KeyValuePair<string, Action>("Reset adjustments", ResetAdjustments),
            });
        }

        private void BuildTools()
        {
            builder.Header("Tools", collapsed: true);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Write diagnostic to log", DiagnosticDump.Dump),
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

        private void FloatSetting(ConfigEntry<float> entry, string label, string tooltip,
                                  float step, float min, float max, string unit, Action afterRelease)
        {
            int positions = Mathf.RoundToInt((max - min) / step) + 1;
            string format = NumberFormat(step);

            builder.Slider(label, tooltip, min, max, positions <= 1000 ? positions : 0,
                Mathf.Clamp(entry.Value, min, max),
                x => x.ToString(format, CultureInfo.InvariantCulture) + unit,
                x => SetValue(entry, Mathf.Clamp(Mathf.Round(x / step) * step, min, max)),
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
