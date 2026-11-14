using System;
using System.Xml.Linq;
using Assets.Scripts.Design.UI;
using HarmonyLib;

namespace SP2TextureMod.UI
{
    // The game builds its UI from XML files and finds widget scripts by name inside its
    // own assembly. These three patches add our button, our layout and our script type.

    // 1) Add our button to the designer's main menu (the three-line button at the top right).
    //    FileMenuPanelScript does not override InitializeDesignerPanel, so the base method is patched
    //    and only the menu is acted on.
    [HarmonyPatch(typeof(DesignerPanelScript), "InitializeDesignerPanel")]
    internal static class DesignerMenuButtonPatch
    {
        private static void Postfix(DesignerPanelScript __instance)
        {
            if (!(__instance is FileMenuPanelScript)) return;
            try
            {
                TexturePanel.AddMenuButton(__instance.Widget);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't add the Textures menu button: " + e);
            }
        }
    }

    // 2) When the game asks for our panel's XML, give it ours instead of looking in its files.
    [HarmonyPatch(typeof(Jundroo.Juicy.ResourceLoader), "LoadXml")]
    internal static class PanelLayoutPatch
    {
        private static bool Prefix(string path, ref XElement __result)
        {
            if (path == TexturePanel.PanelXmlPath)
            {
                __result = TexturePanel.BuildLayout();
                return false;                                    // skip the original
            }
            return true;                                         // not ours: run the original
        }
    }

    // 3) The layout names our script class. The game only searches its own assembly,
    //    so when that fails for one of our names, we answer.
    [HarmonyPatch(typeof(Jundroo.Juicy.ResourceLoader), "GetScriptType")]
    internal static class PanelScriptTypePatch
    {
        private static void Postfix(string scriptTypeName, ref Type __result)
        {
            if (__result == null && scriptTypeName != null &&
                scriptTypeName.StartsWith("SP2TextureMod."))
            {
                __result = typeof(Plugin).Assembly.GetType(scriptTypeName);
            }
        }
    }
}
