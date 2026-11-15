using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SP2TextureMod
{
    /// <summary>
    /// Read-only check (panel > Tools > Check shaders): is a shader with metallic/smoothness maps available in the build?
    /// Writes [SHADERS] lines to the log. It changes nothing in the game.
    /// </summary>
    internal static class ShaderProbe
    {
        private const string Tag = "[SHADERS] ";

        public static void Run()
        {
            try
            {
                string[] names =
                {
                    "Universal Render Pipeline/Lit",
                    "Universal Render Pipeline/Simple Lit",
                    "Universal Render Pipeline/Complex Lit",
                    "Standard",
                    "Game/Parts/Part - Default"
                };

                for (int i = 0; i < names.Length; i++)
                {
                    Shader shader = Shader.Find(names[i]);
                    if (shader == null)
                    {
                        Plugin.Log.LogInfo(Tag + "Shader.Find('" + names[i] + "') = NOT FOUND");
                        continue;
                    }

                    Describe(shader, "Shader.Find('" + names[i] + "')");
                }

                // Every shader the game has loaded at this moment (the ones in use plus what Unity keeps).
                Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
                List<string> list = new List<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null)
                    {
                        list.Add(all[i].name + (all[i].isSupported ? "" : " (not supported)"));
                    }
                }

                list.Sort(StringComparer.Ordinal);
                Plugin.Log.LogInfo(Tag + all.Length + " shader(s) loaded right now:");
                for (int i = 0; i < list.Count; i++)
                {
                    Plugin.Log.LogInfo(Tag + "  " + list[i]);
                }

                Plugin.Log.LogInfo(Tag + "done. Send this part of the log back.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(Tag + "check failed: " + ex);
            }
        }

        private static void Describe(Shader shader, string how)
        {
            StringBuilder props = new StringBuilder();
            bool metallicMap = false;
            bool specMap = false;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                {
                    if (props.Length > 0) props.Append(", ");
                    props.Append(name);
                }

                if (name == "_MetallicGlossMap") metallicMap = true;
                if (name == "_SpecGlossMap") specMap = true;
            }

            Plugin.Log.LogInfo(Tag + how + " = FOUND '" + shader.name + "', supported=" + shader.isSupported + ", passes=" + shader.passCount
                + ", _MetallicGlossMap=" + metallicMap + ", _SpecGlossMap=" + specMap + " | texture properties: " + props);
        }
    }
}
