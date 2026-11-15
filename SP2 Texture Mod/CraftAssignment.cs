using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// What the mod remembers about one craft: which texture set is on it and where the model sits on it.
    /// One small text file per craft name, in &lt;TextureMod folder&gt;/Crafts. It is written when a texture is
    /// applied in the designer and read when that craft is opened in the designer or flown. The craft file
    /// itself is never touched. The file lists plain "key=value" lines, so it can be read (or deleted) by hand.
    /// </summary>
    internal sealed class CraftAssignment
    {
        public const int FormatVersion = 1;

        // The files used (names inside the texture folder; empty = none).
        public string Image = "";
        public string Model = "";
        public string Normal = "";
        public string Occlusion = "";
        public string Height = "";
        public string Specular = "";
        public string MetallicMap = "";
        public string RoughnessMap = "";
        public bool UseLit;          // drawn with the URP Lit shader (per-pixel metallic and smoothness)
        public float MetalAverage;   // averages of the two maps, for the part shader (one value for the craft)
        public float RoughAverage;

        public float TilesPerMeter = 1f;
        public bool FlipModelX = true;

        // Where the model sits on the craft: the final model-to-craft matrix (the automatic fit and the manual
        // adjustment together), in the aircraft's own space. The pieces it was made of are kept as well, so the
        // panel shows the same sliders again.
        public bool HasAlignment;
        public Matrix4x4 ObjToCraft = Matrix4x4.identity;
        public bool NormalsInverted;
        public bool FrontFaces;
        public bool Project = true;
        public float RayStart = 0.3f;
        public float FacingWeight = 0.03f;
        public float MaxDistance = 0.75f;
        public float NeutralBeyond;   // vertices farther than this from the model get a neutral color (0 = off)
        public float PartRefine;      // largest per-renderer correction in meters (0 = off)
        public float NeutralU;
        public float NeutralV;
        public int Orientation;
        public Vector3 Offset;
        public Vector3 Rotation;
        public float AdjustScale = 1f;

        // Maps and material.
        public float NormalStrength = 1f;
        public bool FlipNormalGreen;
        public bool RebuildNormalZ = true;
        public bool HeightToNormal;
        public float BumpStrength = 3f;
        public float OcclusionStrength = 1f;
        public bool UseParallax;
        public float ParallaxHeight = 0.02f;
        public float Metallic;
        public float Smoothness;
        public float SpecularAverage;
        public float SpecularGain = 2f;
        public int SpecularKind; // AtlasMaterial.SpecularKind: how the average of the "specular" image is used

        // The size of the craft when the alignment was made (to notice that the craft changed since).
        public bool HasBounds;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;

        public static string FolderPath
        {
            get { return System.IO.Path.Combine(TextureLibrary.Folder, "Crafts"); }
        }

        /// <summary>Path of the file for a craft name (the name is made safe for a file name).</summary>
        public static string FilePathFor(string craftName)
        {
            return System.IO.Path.Combine(FolderPath, SafeName(craftName) + ".txt");
        }

        /// <summary>Path of the saved texture coordinates of a craft (see UvCache).</summary>
        public static string UvFilePathFor(string craftName)
        {
            return System.IO.Path.Combine(FolderPath, SafeName(craftName) + ".uv");
        }

        public static bool Exists(string craftName)
        {
            return !string.IsNullOrEmpty(craftName) && File.Exists(FilePathFor(craftName));
        }

        /// <summary>The saved assignment of a craft, or null if there is none (or it cannot be read).</summary>
        public static CraftAssignment Load(string craftName)
        {
            if (string.IsNullOrEmpty(craftName))
            {
                return null;
            }

            string path = FilePathFor(craftName);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
                }

                string stored;
                if (values.TryGetValue("craft", out stored)
                    && !string.Equals(stored, craftName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogWarning("[CRAFT] " + path + " belongs to the craft '" + stored + "', not '" + craftName + "': ignored");
                    return null;
                }

                CraftAssignment a = new CraftAssignment();
                a.Image = Text(values, "image", "");
                a.Model = Text(values, "model", "");
                a.Normal = Text(values, "normal", "");
                a.Occlusion = Text(values, "occlusion", "");
                a.Height = Text(values, "height", "");
                a.Specular = Text(values, "specular", "");
                a.MetallicMap = Text(values, "metallicMap", "");
                a.RoughnessMap = Text(values, "roughnessMap", "");
                a.UseLit = Flag(values, "useLit", false);
                a.MetalAverage = Number(values, "metalAverage", 0f);
                a.RoughAverage = Number(values, "roughAverage", 0f);
                a.TilesPerMeter = Number(values, "tilesPerMeter", 1f);
                a.FlipModelX = Flag(values, "flipModelX", true);

                bool alignmentWanted = Flag(values, "hasAlignment", false);
                bool alignmentValid;
                a.ObjToCraft = ReadMatrix(values, "objToCraft", alignmentWanted, out alignmentValid);
                a.HasAlignment = alignmentValid;
                a.NormalsInverted = Flag(values, "normalsInverted", false);
                a.FrontFaces = Flag(values, "frontFaces", false);
                a.Project = Flag(values, "project", true);
                a.RayStart = Number(values, "rayStart", 0.3f);
                a.FacingWeight = Number(values, "facingWeight", 0.03f);
                a.MaxDistance = Number(values, "maxDistance", 0.75f);
                a.NeutralBeyond = Number(values, "neutralBeyond", 0f);
                a.PartRefine = Number(values, "partRefine", 0f);
                a.NeutralU = Number(values, "neutralU", 0f);
                a.NeutralV = Number(values, "neutralV", 0f);
                a.Orientation = (int)Number(values, "orientation", 0f);
                a.Offset = ReadVector(values, "offset", Vector3.zero);
                a.Rotation = ReadVector(values, "rotation", Vector3.zero);
                a.AdjustScale = Number(values, "scale", 1f);

                a.NormalStrength = Number(values, "normalStrength", 1f);
                a.FlipNormalGreen = Flag(values, "flipNormalGreen", false);
                a.RebuildNormalZ = Flag(values, "rebuildNormalZ", true);
                a.HeightToNormal = Flag(values, "heightToNormal", false);
                a.BumpStrength = Number(values, "bumpStrength", 3f);
                a.OcclusionStrength = Number(values, "occlusionStrength", 1f);
                a.UseParallax = Flag(values, "useParallax", false);
                a.ParallaxHeight = Number(values, "parallaxHeight", 0.02f);
                a.Metallic = Number(values, "metallic", 0f);
                a.Smoothness = Number(values, "smoothness", 0f);
                a.SpecularAverage = Number(values, "specularAverage", 0f);
                a.SpecularGain = Number(values, "specularGain", 2f);
                a.SpecularKind = (int)Number(values, "specularKind", 0f);

                string boundsCenter;
                string boundsSize;
                if (values.TryGetValue("boundsCenter", out boundsCenter) && values.TryGetValue("boundsSize", out boundsSize))
                {
                    a.BoundsCenter = ReadVector(values, "boundsCenter", Vector3.zero);
                    a.BoundsSize = ReadVector(values, "boundsSize", Vector3.zero);
                    a.HasBounds = a.BoundsSize.sqrMagnitude > 0f;
                }

                if (a.Image.Length == 0)
                {
                    Plugin.Log.LogWarning("[CRAFT] " + path + " names no image: ignored");
                    return null;
                }

                return a;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[CRAFT] could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Writes the file for the craft. Returns false (and logs why) if it could not be written.</summary>
        public bool Save(string craftName)
        {
            if (string.IsNullOrEmpty(craftName))
            {
                return false;
            }

            string path = FilePathFor(craftName);
            try
            {
                Directory.CreateDirectory(FolderPath);

                StringBuilder sb = new StringBuilder();
                sb.Append("# SP2 Texture Mod: the texture saved for this craft. Written by the mod when a texture is applied in the designer.\r\n");
                sb.Append("# Delete this file (or use Remove in the Textures panel) to forget it. The craft file itself is never changed.\r\n");
                Put(sb, "format", FormatVersion.ToString(CultureInfo.InvariantCulture));
                Put(sb, "craft", craftName.Trim());
                Put(sb, "image", Image);
                Put(sb, "model", Model);
                Put(sb, "normal", Normal);
                Put(sb, "occlusion", Occlusion);
                Put(sb, "height", Height);
                Put(sb, "specular", Specular);
                Put(sb, "metallicMap", MetallicMap);
                Put(sb, "roughnessMap", RoughnessMap);
                Put(sb, "useLit", B(UseLit));
                Put(sb, "metalAverage", N(MetalAverage));
                Put(sb, "roughAverage", N(RoughAverage));
                Put(sb, "tilesPerMeter", N(TilesPerMeter));
                Put(sb, "flipModelX", B(FlipModelX));

                Put(sb, "hasAlignment", B(HasAlignment));
                if (HasAlignment)
                {
                    StringBuilder matrix = new StringBuilder();
                    for (int i = 0; i < 16; i++)
                    {
                        if (i > 0)
                        {
                            matrix.Append(',');
                        }

                        matrix.Append(N(ObjToCraft[i]));
                    }

                    Put(sb, "objToCraft", matrix.ToString());
                }

                Put(sb, "normalsInverted", B(NormalsInverted));
                Put(sb, "frontFaces", B(FrontFaces));
                Put(sb, "project", B(Project));
                Put(sb, "rayStart", N(RayStart));
                Put(sb, "facingWeight", N(FacingWeight));
                Put(sb, "maxDistance", N(MaxDistance));
                Put(sb, "neutralBeyond", N(NeutralBeyond));
                Put(sb, "partRefine", N(PartRefine));
                Put(sb, "neutralU", N(NeutralU));
                Put(sb, "neutralV", N(NeutralV));
                Put(sb, "orientation", Orientation.ToString(CultureInfo.InvariantCulture));
                Put(sb, "offset", V(Offset));
                Put(sb, "rotation", V(Rotation));
                Put(sb, "scale", N(AdjustScale));

                Put(sb, "normalStrength", N(NormalStrength));
                Put(sb, "flipNormalGreen", B(FlipNormalGreen));
                Put(sb, "rebuildNormalZ", B(RebuildNormalZ));
                Put(sb, "heightToNormal", B(HeightToNormal));
                Put(sb, "bumpStrength", N(BumpStrength));
                Put(sb, "occlusionStrength", N(OcclusionStrength));
                Put(sb, "useParallax", B(UseParallax));
                Put(sb, "parallaxHeight", N(ParallaxHeight));
                Put(sb, "metallic", N(Metallic));
                Put(sb, "smoothness", N(Smoothness));
                Put(sb, "specularAverage", N(SpecularAverage));
                Put(sb, "specularGain", N(SpecularGain));
                Put(sb, "specularKind", SpecularKind.ToString(CultureInfo.InvariantCulture));

                if (HasBounds)
                {
                    Put(sb, "boundsCenter", V(BoundsCenter));
                    Put(sb, "boundsSize", V(BoundsSize));
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[CRAFT] could not write " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Forgets the craft's texture. Returns true if there was a file.</summary>
        public static bool Delete(string craftName)
        {
            if (string.IsNullOrEmpty(craftName))
            {
                return false;
            }

            string path = FilePathFor(craftName);
            try
            {
                string uv = UvFilePathFor(craftName);
                if (File.Exists(uv))
                {
                    File.Delete(uv);
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[CRAFT] could not delete " + path + ": " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Puts the settings of the assignment into the plugin's configuration (what the panel shows) and selects
        /// its files in the library. Returns false, with the reason, if its image or model is no longer in the folder.
        /// </summary>
        public bool ApplyToConfig(out string problem)
        {
            if (!TextureLibrary.Select(this, out problem))
            {
                return false;
            }

            Plugin.Set(Plugin.TilesPerMeter, TilesPerMeter);
            Plugin.Set(Plugin.FlipModelX, FlipModelX);
            Plugin.Set(Plugin.ProjectAlongNormals, Project);
            Plugin.Set(Plugin.RayStart, RayStart);
            Plugin.Set(Plugin.FacingWeight, FacingWeight);
            Plugin.Set(Plugin.MaxMatchDistance, MaxDistance);
            Plugin.Set(Plugin.NeutralFarVertices, NeutralBeyond > 0f);
            Plugin.Set(Plugin.RefineEachPart, PartRefine > 0f);
            if (PartRefine > 0f)
            {
                Plugin.Set(Plugin.PartRefineMaxShift, PartRefine);
            }
            Plugin.Set(Plugin.AdjustOffsetX, Offset.x);
            Plugin.Set(Plugin.AdjustOffsetY, Offset.y);
            Plugin.Set(Plugin.AdjustOffsetZ, Offset.z);
            Plugin.Set(Plugin.AdjustRotationX, Rotation.x);
            Plugin.Set(Plugin.AdjustRotationY, Rotation.y);
            Plugin.Set(Plugin.AdjustRotationZ, Rotation.z);
            Plugin.Set(Plugin.AdjustScale, AdjustScale);

            Plugin.Set(Plugin.NormalStrength, NormalStrength);
            Plugin.Set(Plugin.FlipNormalGreen, FlipNormalGreen);
            Plugin.Set(Plugin.RebuildNormalZ, RebuildNormalZ);
            Plugin.Set(Plugin.HeightToNormal, HeightToNormal);
            Plugin.Set(Plugin.BumpStrength, BumpStrength);
            Plugin.Set(Plugin.OcclusionStrength, OcclusionStrength);
            Plugin.Set(Plugin.UseParallax, UseParallax);
            Plugin.Set(Plugin.ParallaxHeight, ParallaxHeight);
            Plugin.Set(Plugin.Metallic, Metallic);
            Plugin.Set(Plugin.Smoothness, Smoothness);
            Plugin.Set(Plugin.SpecularGain, SpecularGain);
            Plugin.Set(Plugin.UseLitShader, UseLit);
            Plugin.SaveConfig();
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static string SafeName(string craftName)
        {
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in craftName.Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string name = sb.ToString().TrimEnd('.', ' ');
            if (name.Length == 0)
            {
                name = "_";
            }

            if (name.Length > 120)
            {
                name = name.Substring(0, 120);
            }

            // Windows does not allow files called CON, PRN, AUX, NUL, COM1.., LPT1.. whatever the extension.
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL"
                || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(upper[3])))
            {
                name += "_";
            }

            return name;
        }

        private static void Put(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(value ?? "").Append("\r\n");
        }

        private static string N(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string B(bool value)
        {
            return value ? "true" : "false";
        }

        private static string V(Vector3 v)
        {
            return N(v.x) + "," + N(v.y) + "," + N(v.z);
        }

        private static string Text(Dictionary<string, string> values, string key, string fallback)
        {
            string text;
            return values.TryGetValue(key, out text) ? text : fallback;
        }

        private static float Number(Dictionary<string, string> values, string key, float fallback)
        {
            string text;
            float number;
            if (values.TryGetValue(key, out text)
                && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !float.IsNaN(number) && !float.IsInfinity(number))
            {
                return number;
            }

            return fallback;
        }

        private static bool Flag(Dictionary<string, string> values, string key, bool fallback)
        {
            string text;
            if (!values.TryGetValue(key, out text))
            {
                return fallback;
            }

            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
        }

        private static Vector3 ReadVector(Dictionary<string, string> values, string key, Vector3 fallback)
        {
            string text;
            if (!values.TryGetValue(key, out text))
            {
                return fallback;
            }

            string[] parts = text.Split(',');
            if (parts.Length != 3)
            {
                return fallback;
            }

            float x, y, z;
            if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return new Vector3(x, y, z);
            }

            return fallback;
        }

        // 16 numbers, column by column (the order of Matrix4x4's indexer). Not valid => the identity and valid = false.
        private static Matrix4x4 ReadMatrix(Dictionary<string, string> values, string key, bool wanted, out bool valid)
        {
            valid = false;
            string text;
            if (!wanted || !values.TryGetValue(key, out text))
            {
                return Matrix4x4.identity;
            }

            string[] parts = text.Split(',');
            if (parts.Length != 16)
            {
                return Matrix4x4.identity;
            }

            Matrix4x4 matrix = Matrix4x4.identity;
            for (int i = 0; i < 16; i++)
            {
                float number;
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    || float.IsNaN(number) || float.IsInfinity(number))
                {
                    return Matrix4x4.identity;
                }

                matrix[i] = number;
            }

            // A model matrix must keep its three axes (a scale of 0 would make the alignment useless).
            if (matrix.GetColumn(0).sqrMagnitude < 1e-12f || matrix.GetColumn(1).sqrMagnitude < 1e-12f
                || matrix.GetColumn(2).sqrMagnitude < 1e-12f)
            {
                return Matrix4x4.identity;
            }

            valid = true;
            return matrix;
        }
    }
}
