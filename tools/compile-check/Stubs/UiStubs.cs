using System;
using System.Collections.Generic;
using UnityEngine;

namespace Jundroo.Juicy.Widgets
{
    public class Widget
    {
        public Widget FindWidget(string name) { return null; }
        public event Action<Widget> Clicked;
        public List<Widget> Widgets { get; }
        public void Destroy() { }
    }
}

namespace Assets.Scripts.UI
{
    using Jundroo.Juicy.Widgets;

    public abstract class PanelDialogScript : MonoBehaviour
    {
        public virtual bool IsModal { get { return true; } }
        public virtual void OnWidgetInitialized(Widget widget) { }
        public void Close() { }
    }
}

namespace Assets.Scripts.UI.Controls
{
    public class ValueTextHolder { public string Text { get; set; } }

    public class TextControl
    {
        public string LabelText { get; set; }
        public ValueTextHolder ValueText { get; }
    }

    public class ToggleControl { }
    public class SliderControl { }
}

namespace SP2TextureMod.UI
{
    using Assets.Scripts.UI.Controls;
    using Jundroo.Juicy.Widgets;

    internal sealed class PanelBuilder
    {
        public PanelBuilder(Widget parent) { }
        public void Header(string title, bool collapsed = false) { }
        public TextControl Text(string label, string value) { return null; }
        public ToggleControl Toggle(string label, string tooltip, bool value, Action<bool> changed) { return null; }
        public SliderControl Slider(string label, string tooltip, float min, float max, int steps,
                                    float value, Func<float, string> format,
                                    Action<float> changed, Action<float> released) { return null; }
        public void Buttons(IList<KeyValuePair<string, Action>> buttons) { }
    }
}
