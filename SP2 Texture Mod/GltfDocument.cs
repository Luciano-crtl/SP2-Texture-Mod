using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SP2TextureMod
{
    /// <summary>
    /// Reads a glTF 2.0 file (.glb with everything inside, or .gltf with embedded or neighbouring buffers and images).
    /// It gives what the mod needs from a model: the triangles with their texture coordinates (for the UV transfer) and the
    /// images of the material that covers most of the model (base color, normal, occlusion, metallic-roughness), which are
    /// written into the texture folder's cache so the rest of the mod treats them like any other image files.
    /// Not supported (reported): Draco and meshopt compression, sparse accessors, triangle strips and fans.
    /// </summary>
    internal sealed class GltfDocument
    {
        /// <summary>The images of the material that was chosen, as indexes into the file's image list (-1 = none).</summary>
        internal sealed class Primary
        {
            public int BaseColor = -1;
            public int Normal = -1;
            public int Occlusion = -1;
            public int MetalRough = -1;
            public int Triangles;
            public string MaterialName = "";
        }

        /// <summary>The files written for the chosen material, as paths relative to the texture folder (null = none).</summary>
        internal sealed class ExtractedFiles
        {
            public string BaseColor;
            public string Normal;
            public string Occlusion;
            public string MetalRough;
        }

        private sealed class PrimitiveInfo
        {
            public int Mesh;
            public int Position = -1;
            public int Indices = -1;
            public int[] Uv = { -1, -1, -1, -1 };
            public int Material = -1;
            public int Mode = 4;
        }

        private Dictionary<string, object> _root;
        private byte[] _glbBinary;
        private string _directory;
        private string _name;
        private readonly Dictionary<int, byte[]> _buffers = new Dictionary<int, byte[]>();
        private int _unsupportedPrimitives;

        public string Name
        {
            get { return _name; }
        }

        /// <summary>A .glb or .gltf file (by its extension).</summary>
        public static bool IsGltf(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".glb" || extension == ".gltf";
        }

        // ---------------------------------------------------------------- opening

        public static GltfDocument Open(string path, out string error)
        {
            error = null;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                error = "the file could not be read: " + ex.Message;
                return null;
            }

            GltfDocument document = new GltfDocument();
            document._name = Path.GetFileName(path);
            document._directory = Path.GetDirectoryName(path);

            string json;
            if (data.Length >= 12 && data[0] == 'g' && data[1] == 'l' && data[2] == 'T' && data[3] == 'F')
            {
                // GLB: 12-byte header, then chunks (length, type, data): JSON first, the binary chunk after it.
                int position = 12;
                json = null;
                while (position + 8 <= data.Length)
                {
                    int length = BitConverter.ToInt32(data, position);
                    uint type = BitConverter.ToUInt32(data, position + 4);
                    position += 8;
                    if (length < 0 || position + length > data.Length)
                    {
                        error = "the GLB file is cut short";
                        return null;
                    }

                    if (type == 0x4E4F534Au && json == null)
                    {
                        json = Encoding.UTF8.GetString(data, position, length);
                    }
                    else if (type == 0x004E4942u && document._glbBinary == null)
                    {
                        byte[] binary = new byte[length];
                        Buffer.BlockCopy(data, position, binary, 0, length);
                        document._glbBinary = binary;
                    }

                    position += (length + 3) & ~3;
                }

                if (json == null)
                {
                    error = "the GLB file has no JSON chunk";
                    return null;
                }
            }
            else
            {
                int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
                json = Encoding.UTF8.GetString(data, skip, data.Length - skip);
            }

            data = null;
            string parseError;
            document._root = Json.Parse(json, out parseError) as Dictionary<string, object>;
            if (document._root == null)
            {
                error = "the glTF JSON could not be read" + (parseError != null ? ": " + parseError : "");
                return null;
            }

            string version = Json.String(Json.Object(document._root, "asset"), "version");
            if (version == null || !version.StartsWith("2", StringComparison.Ordinal))
            {
                error = "only glTF 2.0 is supported (this file says " + (version ?? "nothing") + ")";
                return null;
            }

            List<object> required = Json.Array(document._root, "extensionsRequired");
            if (required != null)
            {
                for (int i = 0; i < required.Count; i++)
                {
                    string extension = required[i] as string;
                    if (extension == "KHR_draco_mesh_compression" || extension == "EXT_meshopt_compression")
                    {
                        error = "the file uses " + extension + " compression, which is not supported: export it without compression";
                        return null;
                    }
                }
            }

            return document;
        }

        // ---------------------------------------------------------------- the material that is used

        /// <summary>
        /// The material whose base color image covers the most triangles of the scene (a model has several materials,
        /// but the mod puts one image on the craft). The other maps are those of the first material that uses that image.
        /// </summary>
        public Primary FindPrimary(out string error)
        {
            error = null;
            List<PrimitiveInfo> primitives = ListPrimitives();
            Dictionary<int, int> trianglesPerImage = new Dictionary<int, int>();
            Dictionary<int, int> firstMaterialOfImage = new Dictionary<int, int>();
            for (int i = 0; i < primitives.Count; i++)
            {
                PrimitiveInfo p = primitives[i];
                if (p.Mode != 4 || p.Position < 0)
                {
                    continue;
                }

                int image = BaseColorImage(p.Material);
                if (image < 0)
                {
                    continue;
                }

                int triangles = TriangleCount(p);
                int sum;
                trianglesPerImage.TryGetValue(image, out sum);
                trianglesPerImage[image] = sum + triangles;
                if (!firstMaterialOfImage.ContainsKey(image))
                {
                    firstMaterialOfImage[image] = p.Material;
                }
            }

            int best = -1;
            int bestCount = 0;
            foreach (KeyValuePair<int, int> pair in trianglesPerImage)
            {
                if (pair.Value > bestCount)
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            }

            if (best < 0)
            {
                error = "no material of the file has a base color texture";
                return null;
            }

            int material = firstMaterialOfImage[best];
            Primary primary = new Primary();
            primary.BaseColor = best;
            primary.Triangles = bestCount;
            primary.MaterialName = NameOf("materials", material);
            primary.Normal = TextureImage(Json.Object(Item("materials", material), "normalTexture"));
            primary.Occlusion = TextureImage(Json.Object(Item("materials", material), "occlusionTexture"));
            primary.MetalRough = TextureImage(Json.Object(Json.Object(Item("materials", material), "pbrMetallicRoughness"), "metallicRoughnessTexture"));
            return primary;
        }

        /// <summary>
        /// Writes the chosen material's images into a folder (an image that is already there with the same size is kept).
        /// File names carry the role (…_baseColor, …_normal, …_metallic_roughness) so the mod's own name rules agree with
        /// the file. Returns the folder-relative paths through toRelative.
        /// </summary>
        public ExtractedFiles Extract(Primary primary, string outputDirectory, string prefix, Func<string, string> toRelative, out string error)
        {
            error = null;
            ExtractedFiles files = new ExtractedFiles();
            try
            {
                Directory.CreateDirectory(outputDirectory);
                bool sameOrm = primary.Occlusion >= 0 && primary.Occlusion == primary.MetalRough;
                files.BaseColor = WriteImage(primary.BaseColor, outputDirectory, prefix + "_baseColor", toRelative);
                files.Normal = WriteImage(primary.Normal, outputDirectory, prefix + "_normal", toRelative);
                if (sameOrm)
                {
                    files.MetalRough = WriteImage(primary.MetalRough, outputDirectory, prefix + "_occlusion_metallic_roughness", toRelative);
                    files.Occlusion = files.MetalRough;
                }
                else
                {
                    files.Occlusion = WriteImage(primary.Occlusion, outputDirectory, prefix + "_occlusion", toRelative);
                    files.MetalRough = WriteImage(primary.MetalRough, outputDirectory, prefix + "_metallic_roughness", toRelative);
                }

                if (files.BaseColor == null)
                {
                    error = "the base color image of the file could not be extracted (PNG and JPEG are supported)";
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = "the images could not be written: " + ex.Message;
                return null;
            }

            return files;
        }

        private string WriteImage(int image, string directory, string baseName, Func<string, string> toRelative)
        {
            if (image < 0)
            {
                return null;
            }

            string extension;
            byte[] bytes = ImageBytes(image, out extension);
            if (bytes == null)
            {
                return null;
            }

            string path = Path.Combine(directory, SafeFileName(baseName) + extension);
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                File.WriteAllBytes(path, bytes);
            }

            return toRelative(path);
        }

        public static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '@' ? '_' : c);
            }

            return sb.ToString();
        }

        // The bytes of an image (embedded in a buffer view, a data URI or a neighbouring file) and its extension.
        private byte[] ImageBytes(int index, out string extension)
        {
            extension = ".png";
            object image = Item("images", index);
            if (image == null)
            {
                return null;
            }

            string mime = Json.String(image, "mimeType");
            string uri = Json.String(image, "uri");
            byte[] bytes = null;
            if (uri != null)
            {
                bytes = ReadUri(uri);
                if (mime == null)
                {
                    string ext = Path.GetExtension(StripQuery(uri)).ToLowerInvariant();
                    mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : (ext == ".png" ? "image/png" : "image/" + ext.TrimStart('.'));
                }
            }
            else if (Json.Has(image, "bufferView"))
            {
                int view = Json.Int(image, "bufferView", -1);
                int offset;
                int length;
                byte[] buffer = ViewBuffer(view, out offset, out length);
                if (buffer != null)
                {
                    bytes = new byte[length];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, length);
                }
            }

            if (bytes == null)
            {
                return null;
            }

            // Look at the first bytes too: the mime type of an exported file is sometimes wrong.
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            {
                extension = ".png";
            }
            else if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                extension = ".jpg";
            }
            else
            {
                Plugin.Log.LogWarning("[GLTF] image " + index + " is '" + mime + "' (not PNG or JPEG): not supported, export the textures as PNG or JPEG");
                return null;
            }

            return bytes;
        }

        // ---------------------------------------------------------------- the geometry

        /// <summary>
        /// The triangles of the primitives whose material uses the primary base color image, as a triangle soup with one UV per
        /// corner. flipX mirrors the model like the OBJ reader does (glTF is right-handed, Unity left-handed).
        /// </summary>
        public ObjModel BuildModel(Primary primary, bool flipX, out string error)
        {
            error = null;
            List<Vector3> ta = new List<Vector3>(primary.Triangles);
            List<Vector3> tb = new List<Vector3>(primary.Triangles);
            List<Vector3> tc = new List<Vector3>(primary.Triangles);
            List<Vector2> ua = new List<Vector2>(primary.Triangles);
            List<Vector2> ub = new List<Vector2>(primary.Triangles);
            List<Vector2> uc = new List<Vector2>(primary.Triangles);
            List<bool> complete = new List<bool>(primary.Triangles);
            int unreadable = 0;

            foreach (KeyValuePair<PrimitiveInfo, double[]> instance in Instances())
            {
                PrimitiveInfo p = instance.Key;
                if (p.Mode != 4 || p.Position < 0 || BaseColorImage(p.Material) != primary.BaseColor)
                {
                    continue;
                }

                int uvSet = Math.Max(0, Math.Min(3, TexCoordOfBaseColor(p.Material)));
                int uvAccessor = p.Uv[uvSet];
                int positionComponents;
                int positionCount;
                float[] positions = ReadFloats(p.Position, out positionComponents, out positionCount);
                if (positions == null || positionComponents < 3)
                {
                    unreadable++;
                    continue;
                }

                float[] uvs = null;
                int uvComponents = 0;
                int uvCount = 0;
                if (uvAccessor >= 0)
                {
                    uvs = ReadFloats(uvAccessor, out uvComponents, out uvCount);
                }

                if (uvs == null || uvComponents < 2 || uvCount < positionCount)
                {
                    unreadable++;
                    continue;
                }

                int[] indices = p.Indices >= 0 ? ReadIndices(p.Indices) : null;
                int corners = indices != null ? indices.Length : positionCount;
                double[] m = instance.Value;
                for (int c = 0; c + 2 < corners; c += 3)
                {
                    int i0 = indices != null ? indices[c] : c;
                    int i1 = indices != null ? indices[c + 1] : c + 1;
                    int i2 = indices != null ? indices[c + 2] : c + 2;
                    if (i0 >= positionCount || i1 >= positionCount || i2 >= positionCount || i0 < 0 || i1 < 0 || i2 < 0)
                    {
                        continue;
                    }

                    ObjModel.AddTriangle(ta, tb, tc, ua, ub, uc,
                        Transform(m, positions, i0, positionComponents), Transform(m, positions, i1, positionComponents),
                        Transform(m, positions, i2, positionComponents),
                        Uv(uvs, i0, uvComponents), Uv(uvs, i1, uvComponents), Uv(uvs, i2, uvComponents), flipX);
                    complete.Add(true);
                }
            }

            if (_unsupportedPrimitives > 0)
            {
                Plugin.Log.LogWarning("[GLTF] " + _name + ": " + _unsupportedPrimitives + " primitive(s) are triangle strips or fans (not supported) and were left out");
            }

            if (unreadable > 0)
            {
                Plugin.Log.LogWarning("[GLTF] " + _name + ": " + unreadable + " primitive(s) had no readable positions or texture coordinates and were left out");
            }

            return ObjModel.Finish(_name, "GLTF", ta, tb, tc, ua, ub, uc, complete, ta.Count > 0 ? 1 : 0, unreadable, out error);
        }

        // glTF's v runs down from the top of the image; ours runs up from the bottom.
        private static Vector2 Uv(float[] uvs, int index, int components)
        {
            return new Vector2(uvs[index * components], 1f - uvs[index * components + 1]);
        }

        private static Vector3 Transform(double[] m, float[] positions, int index, int components)
        {
            double x = positions[index * components];
            double y = positions[index * components + 1];
            double z = positions[index * components + 2];
            return new Vector3(
                (float)(m[0] * x + m[4] * y + m[8] * z + m[12]),
                (float)(m[1] * x + m[5] * y + m[9] * z + m[13]),
                (float)(m[2] * x + m[6] * y + m[10] * z + m[14]));
        }

        // ---------------------------------------------------------------- scene graph

        private List<PrimitiveInfo> ListPrimitives()
        {
            List<PrimitiveInfo> result = new List<PrimitiveInfo>();
            foreach (KeyValuePair<PrimitiveInfo, double[]> instance in Instances())
            {
                result.Add(instance.Key);
            }

            return result;
        }

        // Every instance of every primitive in the scene, with its world matrix (column-major, 16 numbers).
        private List<KeyValuePair<PrimitiveInfo, double[]>> Instances()
        {
            List<KeyValuePair<PrimitiveInfo, double[]>> result = new List<KeyValuePair<PrimitiveInfo, double[]>>();
            _unsupportedPrimitives = 0;
            List<object> nodes = Json.Array(_root, "nodes");
            List<object> scenes = Json.Array(_root, "scenes");
            List<int> roots = new List<int>();
            if (scenes != null && scenes.Count > 0)
            {
                int sceneIndex = Math.Max(0, Math.Min(scenes.Count - 1, Json.Int(_root, "scene", 0)));
                List<object> sceneNodes = Json.Array(scenes[sceneIndex], "nodes");
                if (sceneNodes != null)
                {
                    foreach (object n in sceneNodes)
                    {
                        if (n is double) roots.Add((int)(double)n);
                    }
                }
            }
            else if (nodes != null)
            {
                // No scene: every node that is nobody's child.
                bool[] child = new bool[nodes.Count];
                for (int i = 0; i < nodes.Count; i++)
                {
                    List<object> kids = Json.Array(nodes[i], "children");
                    if (kids != null)
                    {
                        foreach (object k in kids)
                        {
                            if (k is double && (int)(double)k >= 0 && (int)(double)k < child.Length) child[(int)(double)k] = true;
                        }
                    }
                }

                for (int i = 0; i < child.Length; i++)
                {
                    if (!child[i]) roots.Add(i);
                }
            }
            else if (Json.Array(_root, "meshes") != null)
            {
                // Meshes with no nodes at all: they sit where they are.
                for (int m = 0; m < Json.Array(_root, "meshes").Count; m++)
                {
                    AddMesh(result, m, Identity());
                }

                return result;
            }

            foreach (int root in roots)
            {
                Visit(root, Identity(), result, 0);
            }

            return result;
        }

        private void Visit(int node, double[] parent, List<KeyValuePair<PrimitiveInfo, double[]>> result, int depth)
        {
            object n = Item("nodes", node);
            if (n == null || depth > 100)
            {
                return;
            }

            double[] world = Multiply(parent, LocalMatrix(n));
            if (Json.Has(n, "mesh"))
            {
                AddMesh(result, Json.Int(n, "mesh", -1), world);
            }

            List<object> children = Json.Array(n, "children");
            if (children != null)
            {
                foreach (object c in children)
                {
                    if (c is double) Visit((int)(double)c, world, result, depth + 1);
                }
            }
        }

        private void AddMesh(List<KeyValuePair<PrimitiveInfo, double[]>> result, int mesh, double[] world)
        {
            List<object> primitives = Json.Array(Item("meshes", mesh), "primitives");
            if (primitives == null)
            {
                return;
            }

            foreach (object primitive in primitives)
            {
                PrimitiveInfo p = new PrimitiveInfo();
                p.Mesh = mesh;
                p.Mode = Json.Int(primitive, "mode", 4);
                p.Indices = Json.Int(primitive, "indices", -1);
                p.Material = Json.Int(primitive, "material", -1);
                Dictionary<string, object> attributes = Json.Object(primitive, "attributes");
                p.Position = Json.Int(attributes, "POSITION", -1);
                for (int k = 0; k < 4; k++)
                {
                    p.Uv[k] = Json.Int(attributes, "TEXCOORD_" + k, -1);
                }

                if (p.Mode == 5 || p.Mode == 6)
                {
                    _unsupportedPrimitives++;
                }

                result.Add(new KeyValuePair<PrimitiveInfo, double[]>(p, world));
            }
        }

        private static double[] Identity()
        {
            return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        // a * b, column-major.
        private static double[] Multiply(double[] a, double[] b)
        {
            double[] r = new double[16];
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        sum += a[k * 4 + row] * b[col * 4 + k];
                    }

                    r[col * 4 + row] = sum;
                }
            }

            return r;
        }

        // The node's own matrix: "matrix", or translation * rotation (quaternion) * scale.
        private static double[] LocalMatrix(object node)
        {
            List<object> matrix = Json.Array(node, "matrix");
            if (matrix != null && matrix.Count == 16)
            {
                double[] m = new double[16];
                for (int i = 0; i < 16; i++)
                {
                    m[i] = matrix[i] is double ? (double)matrix[i] : 0.0;
                }

                return m;
            }

            double[] t = Vector(Json.Array(node, "translation"), 3, 0, 0, 0, 0);
            double[] q = Vector(Json.Array(node, "rotation"), 4, 0, 0, 0, 1);
            double[] s = Vector(Json.Array(node, "scale"), 3, 1, 1, 1, 0);
            double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (len < 1e-12)
            {
                q = new double[] { 0, 0, 0, 1 };
                len = 1;
            }

            double x = q[0] / len, y = q[1] / len, z = q[2] / len, w = q[3] / len;
            double[] r = new double[16];
            r[0] = (1 - 2 * (y * y + z * z)) * s[0];
            r[1] = (2 * (x * y + z * w)) * s[0];
            r[2] = (2 * (x * z - y * w)) * s[0];
            r[4] = (2 * (x * y - z * w)) * s[1];
            r[5] = (1 - 2 * (x * x + z * z)) * s[1];
            r[6] = (2 * (y * z + x * w)) * s[1];
            r[8] = (2 * (x * z + y * w)) * s[2];
            r[9] = (2 * (y * z - x * w)) * s[2];
            r[10] = (1 - 2 * (x * x + y * y)) * s[2];
            r[12] = t[0];
            r[13] = t[1];
            r[14] = t[2];
            r[15] = 1;
            return r;
        }

        private static double[] Vector(List<object> list, int count, double d0, double d1, double d2, double d3)
        {
            double[] result = { d0, d1, d2, d3 };
            if (list != null && list.Count >= count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (list[i] is double) result[i] = (double)list[i];
                }
            }

            return result;
        }

        // ---------------------------------------------------------------- materials and textures

        private int BaseColorImage(int material)
        {
            if (material < 0)
            {
                return -1;
            }

            return TextureImage(Json.Object(Json.Object(Item("materials", material), "pbrMetallicRoughness"), "baseColorTexture"));
        }

        private int TexCoordOfBaseColor(int material)
        {
            if (material < 0)
            {
                return 0;
            }

            return Json.Int(Json.Object(Json.Object(Item("materials", material), "pbrMetallicRoughness"), "baseColorTexture"), "texCoord", 0);
        }

        // The image index behind a textureInfo ({ "index": texture }).
        private int TextureImage(Dictionary<string, object> info)
        {
            if (info == null)
            {
                return -1;
            }

            object texture = Item("textures", Json.Int(info, "index", -1));
            return texture == null ? -1 : Json.Int(texture, "source", -1);
        }

        private int TriangleCount(PrimitiveInfo p)
        {
            object accessor = Item("accessors", p.Indices >= 0 ? p.Indices : p.Position);
            return accessor == null ? 0 : Json.Int(accessor, "count", 0) / 3;
        }

        private string NameOf(string list, int index)
        {
            return Json.String(Item(list, index), "name") ?? (list + "[" + index + "]");
        }

        private object Item(string list, int index)
        {
            List<object> items = Json.Array(_root, list);
            return items != null && index >= 0 && index < items.Count ? items[index] : null;
        }

        // ---------------------------------------------------------------- accessors and buffers

        private float[] ReadFloats(int accessorIndex, out int components, out int count)
        {
            components = 0;
            count = 0;
            object accessor = Item("accessors", accessorIndex);
            if (accessor == null || Json.Has(accessor, "sparse"))
            {
                return null;
            }

            string type = Json.String(accessor, "type");
            components = type == "SCALAR" ? 1 : (type == "VEC2" ? 2 : (type == "VEC3" ? 3 : (type == "VEC4" ? 4 : 0)));
            count = Json.Int(accessor, "count", 0);
            int componentType = Json.Int(accessor, "componentType", 0);
            bool normalized = Json.Bool(accessor, "normalized", false);
            int size = ComponentSize(componentType);
            if (components == 0 || size == 0 || count < 0)
            {
                return null;
            }

            float[] result = new float[count * components];
            if (!Json.Has(accessor, "bufferView"))
            {
                return result; // no data: zeros
            }

            int offset;
            int length;
            int viewIndex = Json.Int(accessor, "bufferView", -1);
            byte[] buffer = ViewBuffer(viewIndex, out offset, out length);
            if (buffer == null)
            {
                return null;
            }

            int stride = Json.Int(Item("bufferViews", viewIndex), "byteStride", 0);
            if (stride == 0)
            {
                stride = size * components;
            }

            int start = offset + Json.Int(accessor, "byteOffset", 0);
            if (count > 0 && start + (long)(count - 1) * stride + size * components > buffer.Length)
            {
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    result[i * components + c] = ReadComponent(buffer, start + i * stride + c * size, componentType, normalized);
                }
            }

            return result;
        }

        private int[] ReadIndices(int accessorIndex)
        {
            object accessor = Item("accessors", accessorIndex);
            if (accessor == null || Json.Has(accessor, "sparse") || !Json.Has(accessor, "bufferView"))
            {
                return null;
            }

            int count = Json.Int(accessor, "count", 0);
            int componentType = Json.Int(accessor, "componentType", 0);
            int size = ComponentSize(componentType);
            int offset;
            int length;
            int viewIndex = Json.Int(accessor, "bufferView", -1);
            byte[] buffer = ViewBuffer(viewIndex, out offset, out length);
            if (buffer == null || size == 0)
            {
                return null;
            }

            int stride = Json.Int(Item("bufferViews", viewIndex), "byteStride", 0);
            if (stride == 0)
            {
                stride = size;
            }

            int start = offset + Json.Int(accessor, "byteOffset", 0);
            if (count > 0 && start + (long)(count - 1) * stride + size > buffer.Length)
            {
                return null;
            }

            int[] result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int at = start + i * stride;
                result[i] = componentType == 5125 ? (int)BitConverter.ToUInt32(buffer, at)
                    : (componentType == 5123 ? BitConverter.ToUInt16(buffer, at) : buffer[at]);
            }

            return result;
        }

        private static int ComponentSize(int componentType)
        {
            switch (componentType)
            {
                case 5120: case 5121: return 1;
                case 5122: case 5123: return 2;
                case 5125: case 5126: return 4;
                default: return 0;
            }
        }

        private static float ReadComponent(byte[] b, int at, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case 5126: return BitConverter.ToSingle(b, at);
                case 5121: return normalized ? b[at] / 255f : b[at];
                case 5120: return normalized ? Math.Max((sbyte)b[at] / 127f, -1f) : (sbyte)b[at];
                case 5123: return normalized ? BitConverter.ToUInt16(b, at) / 65535f : BitConverter.ToUInt16(b, at);
                case 5122: return normalized ? Math.Max(BitConverter.ToInt16(b, at) / 32767f, -1f) : BitConverter.ToInt16(b, at);
                case 5125: return BitConverter.ToUInt32(b, at);
                default: return 0f;
            }
        }

        // The buffer a buffer view lives in, with the view's offset and length.
        private byte[] ViewBuffer(int viewIndex, out int offset, out int length)
        {
            offset = 0;
            length = 0;
            object view = Item("bufferViews", viewIndex);
            if (view == null)
            {
                return null;
            }

            byte[] buffer = GetBuffer(Json.Int(view, "buffer", -1));
            if (buffer == null)
            {
                return null;
            }

            offset = Json.Int(view, "byteOffset", 0);
            length = Json.Int(view, "byteLength", 0);
            if (offset < 0 || length < 0 || offset + (long)length > buffer.Length)
            {
                return null;
            }

            return buffer;
        }

        private byte[] GetBuffer(int index)
        {
            byte[] cached;
            if (_buffers.TryGetValue(index, out cached))
            {
                return cached;
            }

            object buffer = Item("buffers", index);
            byte[] bytes = null;
            if (buffer != null)
            {
                string uri = Json.String(buffer, "uri");
                bytes = uri == null ? _glbBinary : ReadUri(uri);
            }

            _buffers[index] = bytes;
            return bytes;
        }

        // A data URI (base64) or a file next to the model.
        private byte[] ReadUri(string uri)
        {
            try
            {
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = uri.IndexOf(',');
                    return comma < 0 ? null : Convert.FromBase64String(uri.Substring(comma + 1));
                }

                string relative = Uri.UnescapeDataString(StripQuery(uri));
                string path = Path.Combine(_directory ?? "", relative.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[GLTF] could not read '" + (uri.Length > 60 ? uri.Substring(0, 60) + "..." : uri) + "': " + ex.Message);
                return null;
            }
        }

        private static string StripQuery(string uri)
        {
            int q = uri.IndexOfAny(new[] { '?', '#' });
            return q < 0 ? uri : uri.Substring(0, q);
        }
    }
}
