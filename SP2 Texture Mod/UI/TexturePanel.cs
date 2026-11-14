using System;
using System.Xml.Linq;
using Jundroo.Juicy.Widgets;

namespace SP2TextureMod.UI
{
    // Opens and closes the panel, adds its button to the designer's main menu and
    // provides the panel's layout. Same technique as the author's SP2 Armor Mod, which adds a
    // button to the flight menu: the game builds its UI from XML and finds scripts by name,
    // so UiPatches hand it our XML and our script type.
    internal static class TexturePanel
    {
        // The path the game "loads" for the panel. UiPatches hands out our own XML for it.
        public const string PanelXmlPath = "Mods/SP2TextureMod/Panel";
        public const string ButtonId = "sp2-texture-mod-menu-button";

        private static TexturePanelScript panel;
        private static ButtonWidget menuButton;

        public static bool IsOpen
        {
            get { return panel != null && !panel.IsClosed; }
        }

        // The designer's menu handlers that are wired by name from the game's XML: the new button
        // is placed right after the first of these that is found (and copies its look).
        private static readonly string[] AnchorHandlers =
        {
            "OnExportClicked", "OnEnvironmentClicked", "OnCraftPropertiesClicked", "OnSettingsClicked", "OnSaveClicked"
        };

        // Called each time the designer's menu is built.
        public static void AddMenuButton(Widget menu)
        {
            if (menu == null || menu.FindWidget(ButtonId) != null) return;

            Widget anchor = null;
            foreach (string handler in AnchorHandlers)
            {
                anchor = FindByClickHandler(menu, handler);
                if (anchor != null) break;
            }
            if (anchor == null) anchor = FindFirstButton(menu);

            Widget parent = anchor != null ? anchor.Parent : menu;

            string buttonClass = "btn btn-default";
            if (anchor != null && anchor.Element != null)
            {
                XAttribute copied = anchor.Element.Attribute("class");
                if (copied != null && !string.IsNullOrEmpty(copied.Value)) buttonClass = copied.Value;
            }

            XElement element = new XElement("Button",
                new XAttribute("id", ButtonId),
                new XAttribute("class", buttonClass),
                new XAttribute("tooltip", "Put your own textures, or a War Thunder model's textures, on the procedural parts of this craft."),
                new XElement("Text", new XAttribute("text", "Textures")));

            ButtonWidget button = parent.Context.CreateWidget(element, parent, parent.Stylesheet) as ButtonWidget;
            if (button == null)
            {
                Plugin.Log.LogWarning("[UI] the Textures menu button could not be created");
                return;
            }

            button.Clicked += (Widget w) => Toggle();
            if (anchor != null) button.SetIndex(anchor.Index + 1);

            menuButton = button;
            panel = null;   // a new designer scene means any old panel is gone
            Plugin.Log.LogInfo("[UI] Textures button added to the designer menu (next to "
                + (anchor != null ? "'" + (anchor.Id ?? anchor.EventClick ?? "?") + "'" : "the end of the menu") + ")");
        }

        private static Widget FindByClickHandler(Widget root, string handler)
        {
            if (string.Equals(root.EventClick, handler, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (Widget child in root.Widgets)
            {
                Widget found = FindByClickHandler(child, handler);
                if (found != null) return found;
            }
            return null;
        }

        private static Widget FindFirstButton(Widget root)
        {
            foreach (Widget child in root.Widgets)
            {
                if (child is ButtonWidget) return child;
                Widget found = FindFirstButton(child);
                if (found != null) return found;
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
                panel = Assets.Scripts.Game.Instance.UserInterface.CreateDialog<TexturePanelScript>(PanelXmlPath);
                panel.Closed += (Assets.Scripts.UI.IDialog d) =>
                {
                    panel = null;
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't open the Textures panel: " + e);
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
