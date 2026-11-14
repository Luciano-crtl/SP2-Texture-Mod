using System;
using System.Xml.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SP2TextureMod.UI
{
    // Opens and closes the panel and puts its button in the designer's main menu.
    //
    // The panel is a dialog built with the game's UI library (Juicy), like the author's SP2 Armor Mod does
    // for the flight menu: the game builds its UI from XML and finds scripts by name, so UiPatches hand it
    // our XML and our script type.
    //
    // The menu button is added the way the open-source SP2 Reference Model mod does it, which is known to work
    // in the designer: the menu is found by the labels of its buttons, one of them is cloned (it is a plain
    // uGUI Button with a TextMeshPro label) and the clone gets our label and our click action.
    internal static class TexturePanel
    {
        // The path the game "loads" for the panel. UiPatches hands out our own XML for it.
        public const string PanelXmlPath = "Mods/SP2TextureMod/Panel";

        private const string ButtonName = "SP2TextureModMenuButton";
        private const string ButtonLabel = "Textures";

        // Labels of the designer menu's own buttons; the first one found is cloned.
        private static readonly string[] TemplateLabels = { "Designer Tutorials", "Undo History", "Upload Craft" };

        private static TexturePanelScript panel;
        private static GameObject buttonObject;
        private static TMP_Text buttonText;
        private static float nextTry;
        private static int tries;

        public static bool IsOpen
        {
            get { return panel != null && !panel.IsClosed; }
        }

        // Called every frame by the plugin. Waits for the designer's menu to exist, adds the button once,
        // and adds it again if the menu is rebuilt.
        public static void Tick()
        {
            if (Assets.Scripts.Design.Designer.Instance == null)
            {
                // Left the designer: everything of the old scene is gone.
                buttonObject = null;
                buttonText = null;
                panel = null;
                tries = 0;
                return;
            }

            if (buttonObject != null)
            {
                // The game's text component may reset the label of a cloned button: put ours back.
                if (buttonText != null && buttonText.text != ButtonLabel) buttonText.text = ButtonLabel;
                return;
            }

            if (Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 0.5f;
            tries++;

            try
            {
                if (!TryAddMenuButton() && tries == 20)
                {
                    Plugin.Log.LogWarning("[UI] the designer's menu was not found yet (looking for a button labeled "
                        + string.Join(" / ", TemplateLabels) + "). The panel can still be opened with the PanelKey.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[UI] couldn't add the Textures menu button: " + e);
                nextTry = Time.unscaledTime + 5f;
            }
        }

        private static bool TryAddMenuButton()
        {
            Button template = FindTemplate();
            if (template == null) return false;

            Transform parent = template.transform.parent;
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            clone.name = ButtonName;
            clone.SetActive(true);

            Button button = clone.GetComponent<Button>();
            button.interactable = true;
            button.onClick.RemoveAllListeners();   // the copy of the original's click action is dropped
            button.onClick.AddListener(Toggle);

            CanvasGroup group = clone.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            TMP_Text text = clone.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.text = ButtonLabel;
                text.enabled = true;
            }

            // Same height as the button it was copied from.
            float height = ((RectTransform)template.transform).rect.height;
            if (height < 10f) height = 40f;
            LayoutElement layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
            ((RectTransform)clone.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            buttonObject = clone;
            buttonText = text;
            panel = null;
            Plugin.Log.LogInfo("[UI] Textures button added to the designer menu, below the '"
                + (template.GetComponentInChildren<TMP_Text>(true) != null ? template.GetComponentInChildren<TMP_Text>(true).text : template.name)
                + "' button");
            return true;
        }

        // A button of the designer's menu: found through its label, including inactive objects (the menu may be closed).
        private static Button FindTemplate()
        {
            TMP_Text[] texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (string wanted in TemplateLabels)
            {
                foreach (TMP_Text text in texts)
                {
                    if (text == null || text.text == null) continue;
                    if (!string.Equals(text.text.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    Button button = text.GetComponentInParent<Button>(true);
                    if (button != null && button.gameObject.name != ButtonName) return button;
                }
            }
            return null;
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                panel.Close();
                return;
            }

            if (Assets.Scripts.Design.Designer.Instance == null)
            {
                Plugin.Log.LogWarning("[UI] the Textures panel is only available in the designer");
                return;
            }

            try
            {
                Plugin.Log.LogInfo("[UI] opening the Textures panel");
                panel = Assets.Scripts.Game.Instance.UserInterface.CreateDialog<TexturePanelScript>(PanelXmlPath);
                panel.Closed += (Assets.Scripts.UI.IDialog d) =>
                {
                    panel = null;
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[UI] couldn't open the Textures panel: " + e);
                panel = null;
            }
        }

        // The panel layout. Single quotes keep the C# string readable.
        public static XElement BuildLayout()
        {
            string xml = @"
<Xml>
   <Stylesheets>
      <Stylesheet path='Xml/Styles' />
      <Stylesheet>
         <Styles>
            <Style class='tex-dialog' width='560' maxHeightPercent='90' />
         </Styles>
      </Stylesheet>
   </Stylesheets>
   <Widgets>
      <Instance id='tex-dialog' templateId='dialog' class='tex-dialog' contentPadding='7 7 7 7'
                anchor='TopLeft' position='25 -25' script='{SCRIPT}' title='Textures'
                backerVisible='false' buttonRowVisible='false' spacerVisible='false'>
         <Section sectionId='title-buttons'>
            <Button id='tex-close-button' class='btn btn-danger' width='35' height='35' anchor='MiddleRight'>
               <Image sprite='Sprites/Common/IconButtonClose' />
            </Button>
         </Section>
         <Section sectionId='content'>
            <ScrollView fitContentHeight='true' verticalScrollbarVisibility='AutoHideAndExpandViewport'>
               <VerticalLayout id='tex-content' class='vertical-layout'
                               padding='@HalfSpacing @Spacing @HalfSpacing @HalfSpacing' spacing='@Spacing' />
            </ScrollView>
         </Section>
      </Instance>
   </Widgets>
</Xml>";
            return XElement.Parse(xml.Replace("{SCRIPT}", typeof(TexturePanelScript).FullName));
        }
    }
}
