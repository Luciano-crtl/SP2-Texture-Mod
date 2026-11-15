using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SP2TextureMod;

internal static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static void Fill(params string[] names)
    {
        foreach (string f in Directory.GetFiles(TextureLibrary.Folder)) File.Delete(f);
        foreach (string f in names) File.WriteAllText(Path.Combine(TextureLibrary.Folder, f), "x");
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "sp2-logic-tests");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Application.persistentDataPath = root;
        Directory.CreateDirectory(TextureLibrary.Folder);

        MapNames();
        SavedCraftFile();
        ObjReading();
        UvCacheFile();
        GltfAndSets();

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }

    // ------------------------------------------------------------------ names of the map files

    static void MapNames()
    {
        // The folder of the user's War Thunder set (README.txt is written by the mod itself).
        Fill("fw_190d_9_a.jpg", "fw_190d_9_a.obj", "debug_uv_map.png", "fw_190d_9_n_ao.jpg", "fw_190d_9_n_n.jpg", "fw_190d_9_n_s.jpg");
        TextureLibrary.Refresh();
        Check(TextureLibrary.ImageCount == 1, "only the albedo is offered as an image (got " + TextureLibrary.ImageCount + ")");
        Check(TextureLibrary.CurrentImage.FileName == "fw_190d_9_a.jpg", "the image is fw_190d_9_a.jpg");
        Check(TextureLibrary.CurrentModelName == "fw_190d_9_a.obj", "the model is paired by name");
        Check(TextureLibrary.Normal.CurrentName == "fw_190d_9_n_n.jpg", "normal = " + TextureLibrary.Normal.CurrentName);
        Check(TextureLibrary.Occlusion.CurrentName == "fw_190d_9_n_ao.jpg", "occlusion = " + TextureLibrary.Occlusion.CurrentName);
        Check(TextureLibrary.Specular.CurrentName == "fw_190d_9_n_s.jpg", "specular = " + TextureLibrary.Specular.CurrentName);
        Check(TextureLibrary.Height.CurrentName == null, "height = none");
        string readme = Path.Combine(TextureLibrary.Folder, "README.txt");
        Check(File.Exists(readme) && File.ReadAllText(readme).Contains("Readme version 4"), "README written");

        // Stepping through the images keeps the maps on "auto" (an old build saved "none").
        TextureLibrary.StepImage(1);
        Check(Plugin.SelectedNormal.Value == "auto" && Plugin.SelectedSpecular.Value == "auto", "stepping saves auto, not none");
        Check(TextureLibrary.Normal.CurrentName == "fw_190d_9_n_n.jpg", "normal still found after stepping");

        // The usual names.
        Fill("ta152_a.png", "ta152_n.png", "ta152_ao.png", "ta152_h.png", "ta152_spec.png", "ta152_a.obj", "sunset_s.png", "wood.png");
        Plugin.SelectedTexture.Value = "ta152_a.png";
        TextureLibrary.Refresh();
        Check(TextureLibrary.ImageCount == 3, "ta152_a, sunset_s and wood are albedo candidates (got " + TextureLibrary.ImageCount + ")");
        Check(TextureLibrary.Normal.CurrentName == "ta152_n.png" && TextureLibrary.Occlusion.CurrentName == "ta152_ao.png"
            && TextureLibrary.Height.CurrentName == "ta152_h.png" && TextureLibrary.Specular.CurrentName == "ta152_spec.png", "usual endings found");

        // The B-2 set: names without underscores and a glTF metallic-roughness image split by channel.
        Fill("B2BaseColor.png", "B2Normal.png", "B2Metallic-B2Roughness@channels=B.png", "B2Metallic-B2Roughness@channels=G.png",
            "internal_ground_ao_texture.jpeg", "B-2.obj");
        Plugin.SelectedTexture.Value = "B2BaseColor.png";
        TextureLibrary.Refresh();
        Check(TextureLibrary.CurrentImage.FileName == "B2BaseColor.png" && TextureLibrary.ImageCount == 2,
            "B-2: the albedo list holds BaseColor and the ground AO (got " + TextureLibrary.ImageCount + ")");
        Check(TextureLibrary.Normal.CurrentName == "B2Normal.png", "B-2 normal = " + TextureLibrary.Normal.CurrentName);
        Check(TextureLibrary.Metalness.CurrentName == "B2Metallic-B2Roughness@channels=B.png", "B-2 metalness = " + TextureLibrary.Metalness.CurrentName);
        Check(TextureLibrary.Roughness.CurrentName == "B2Metallic-B2Roughness@channels=G.png", "B-2 roughness = " + TextureLibrary.Roughness.CurrentName);
        Check(TextureLibrary.Occlusion.CurrentName == null, "B-2 occlusion: the ground AO of another object is not taken");

        Fill("ta152_a.png", "ta152_n.png", "ta152_ao.png", "ta152_h.png", "ta152_spec.png", "ta152_a.obj", "sunset_s.png", "wood.png");
        Plugin.SelectedTexture.Value = "ta152_a.png";
        TextureLibrary.Refresh();

        // Selecting what a saved assignment names.
        CraftAssignment a = new CraftAssignment();
        a.Image = "wood.png"; a.Model = ""; a.Normal = "ta152_n.png"; a.Specular = "";
        string problem;
        Check(TextureLibrary.Select(a, out problem), "Select with a valid assignment (" + problem + ")");
        Check(TextureLibrary.CurrentImage.FileName == "wood.png" && TextureLibrary.CurrentModelName == null
            && TextureLibrary.Normal.CurrentName == "ta152_n.png" && TextureLibrary.Specular.CurrentName == null, "Select chose the image, no model and the maps");
        a.Image = "gone.png";
        Check(!TextureLibrary.Select(a, out problem) && problem.Contains("gone.png"), "a missing image is reported: " + problem);
        a.Image = "wood.png"; a.Model = "gone.obj";
        Check(!TextureLibrary.Select(a, out problem) && problem.Contains("gone.obj"), "a missing model is reported: " + problem);
    }

    // ------------------------------------------------------------------ the file saved for a craft

    static void SavedCraftFile()
    {
        CraftAssignment a = new CraftAssignment();
        a.Image = "fw_190d_9_a.jpg";
        a.Model = "fw_190d_9_a.obj";
        a.Normal = "fw_190d_9_n_n.jpg";
        a.Occlusion = "fw_190d_9_n_ao.jpg";
        a.Specular = "fw_190d_9_n_s.jpg";
        a.TilesPerMeter = 1.5f;
        a.HasAlignment = true;
        Matrix4x4 m = Matrix4x4.identity;
        m[0] = 0.0123456789f; m[5] = 0.0123456789f; m[10] = 0.0123456789f; m[12] = 1.25f; m[13] = -0.5f; m[14] = 3.75f;
        a.ObjToCraft = m;
        a.NormalsInverted = true;
        a.FrontFaces = true;
        a.RayStart = 0.31f; a.FacingWeight = 0.04f; a.MaxDistance = 0.8f;
        a.Orientation = 2;
        a.Offset = new Vector3(0.01f, -0.02f, 0.03f);
        a.Rotation = new Vector3(1f, 2f, -3f);
        a.AdjustScale = 1.005f;
        a.NormalStrength = 0.8f; a.FlipNormalGreen = true; a.RebuildNormalZ = false; a.HeightToNormal = true;
        a.BumpStrength = 4.5f; a.OcclusionStrength = 0.7f; a.UseParallax = true; a.ParallaxHeight = 0.03f;
        a.Metallic = 0.1f; a.Smoothness = 0.25f; a.SpecularAverage = 0.0412f; a.SpecularGain = 2.5f;
        a.HasBounds = true; a.BoundsCenter = new Vector3(0.1f, 0.2f, 0.3f); a.BoundsSize = new Vector3(9.5f, 3.2f, 10.1f);

        string name = "Fw 190 D-9: ação/teste?"; // accents and characters that are not allowed in file names
        Check(a.Save(name), "Save returns true");
        Check(File.Exists(CraftAssignment.FilePathFor(name)) && CraftAssignment.Exists(name), "the file exists");

        CraftAssignment b = CraftAssignment.Load(name);
        Check(b != null, "Load returns the assignment");
        if (b != null)
        {
            Check(b.Image == a.Image && b.Model == a.Model && b.Normal == a.Normal && b.Occlusion == a.Occlusion
                && b.Specular == a.Specular && b.Height == "", "file names");
            Check(b.TilesPerMeter == 1.5f && b.FlipModelX, "tiling and mirror");
            bool same = b.HasAlignment;
            for (int i = 0; i < 16; i++) same &= b.ObjToCraft[i] == a.ObjToCraft[i];
            Check(same, "the matrix comes back exactly");
            Check(b.NormalsInverted && b.FrontFaces && b.Project, "flags");
            Check(b.RayStart == 0.31f && b.FacingWeight == 0.04f && b.MaxDistance == 0.8f, "ray options");
            Check(b.Orientation == 2 && b.Offset == a.Offset && b.Rotation == a.Rotation && b.AdjustScale == 1.005f, "adjustment");
            Check(b.NormalStrength == 0.8f && b.FlipNormalGreen && !b.RebuildNormalZ && b.HeightToNormal && b.BumpStrength == 4.5f
                && b.OcclusionStrength == 0.7f && b.UseParallax && b.ParallaxHeight == 0.03f, "map settings");
            Check(b.Metallic == 0.1f && b.Smoothness == 0.25f && b.SpecularAverage == 0.0412f && b.SpecularGain == 2.5f, "material settings");
            Check(b.HasBounds && b.BoundsCenter == a.BoundsCenter && b.BoundsSize == a.BoundsSize, "bounds");
        }

        Check(CraftAssignment.Load("Other craft") == null, "another name has nothing");

        // A culture that writes decimal commas must not matter.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
        bool saved = a.Save("culture test");
        CraftAssignment c = CraftAssignment.Load("culture test");
        System.Globalization.CultureInfo.CurrentCulture = previous;
        Check(saved && c != null && c.Smoothness == 0.25f && c.TilesPerMeter == 1.5f, "round trip under pt-BR");

        // Hand-edited garbage is tolerated.
        string bad = CraftAssignment.FilePathFor("bad");
        File.WriteAllText(bad, "image=x.png\nhasAlignment=true\nobjToCraft=1,2,3\nsmoothness=abc\n# comment\nnonsense line\n");
        CraftAssignment d = CraftAssignment.Load("bad");
        Check(d != null && !d.HasAlignment && d.Smoothness == 0f, "a bad matrix and a bad number are ignored");
        File.WriteAllText(bad, "model=only.obj\n");
        Check(CraftAssignment.Load("bad") == null, "a file without an image is ignored");
        File.WriteAllText(bad, "craft=someone else\nimage=x.png\n");
        Check(CraftAssignment.Load("bad") == null, "a file of another craft is ignored");

        Check(CraftAssignment.Delete(name) && !CraftAssignment.Exists(name), "Delete removes it");
        Check(CraftAssignment.FilePathFor("CON").EndsWith("CON_.txt"), "reserved Windows names get an underscore");
    }

    // ------------------------------------------------------------------ the saved texture coordinates

    static Mesh MakeMesh(float shift, int count)
    {
        Mesh mesh = new Mesh();
        for (int i = 0; i < count; i++)
        {
            mesh.V.Add(new Vector3(i * 0.37f + shift, i * i * 0.011f, -i * 0.5f));
            mesh.U.Add(new Vector2(i * 0.1f, 1f - i * 0.05f));
        }

        return mesh;
    }

    static void UvCacheFile()
    {
        string model = Path.Combine(TextureLibrary.Folder, "model.obj");
        File.WriteAllText(model, "v 0 0 0\n");
        Matrix4x4 m = Matrix4x4.identity;
        string a = UvCache.MakeSignature(model, true, m, false, true, true, 0.3f, 0.03f, 0.75f);
        Check(a == UvCache.MakeSignature(model, true, m, false, true, true, 0.3f, 0.03f, 0.75f), "the signature is stable");
        m[12] = 0.5f;
        Check(a != UvCache.MakeSignature(model, true, m, false, true, true, 0.3f, 0.03f, 0.75f), "a moved alignment changes the signature");
        Check(a != UvCache.MakeSignature(model, false, Matrix4x4.identity, false, true, true, 0.3f, 0.03f, 0.75f), "the mirror setting changes it");

        string sig = UvCache.MakeSignature(model, true, Matrix4x4.identity, false, true, true, 0.3f, 0.03f, 0.75f);
        UvCache cache = new UvCache(sig);
        Mesh wing = MakeMesh(0f, 40);
        Mesh tail = MakeMesh(5f, 17);
        cache.Remember(7, 0, wing);
        cache.Remember(7, 1, tail);
        cache.Remember(300, 0, MakeMesh(1f, 9));
        Check(cache.Save("cache craft: wings?"), "the cache is written");

        UvCache back = UvCache.Load("cache craft: wings?", sig);
        Check(back != null && back.Count == 3, "and read back (3 renderers)");
        if (back != null)
        {
            Mesh fresh = MakeMesh(0f, 40);
            fresh.U.Clear();
            for (int i = 0; i < 40; i++) fresh.U.Add(new Vector2(0, 0));
            Check(back.Apply(7, 0, fresh) && fresh.U[13].x == 13 * 0.1f && fresh.U[39].y == 1f - 39 * 0.05f, "the same mesh gets its coordinates");
            Mesh other = MakeMesh(0.5f, 40);
            Check(!back.Apply(7, 0, other), "a mesh with other vertices gets none");
            Check(!back.Apply(7, 5, MakeMesh(0f, 40)), "an unknown renderer gets none");
            Check(!back.Apply(7, 0, MakeMesh(0f, 41)), "another vertex count gets none");
            Mesh t = MakeMesh(5f, 17);
            Check(back.Apply(7, 1, t) && t.U.Count == 17, "a second renderer of the same part");
        }

        Check(UvCache.Load("cache craft: wings?", sig + "x") == null, "another signature: not used");
        Check(UvCache.Load("no such craft", sig) == null, "no file: nothing");
        File.WriteAllBytes(CraftAssignment.UvFilePathFor("broken"), new byte[] { 1, 2, 3 });
        Check(UvCache.Load("broken", sig) == null, "a damaged file: nothing");
        CraftAssignment saved = new CraftAssignment();
        saved.Image = "x.png";
        saved.Save("cache craft: wings?");
        Check(CraftAssignment.Delete("cache craft: wings?") && !File.Exists(CraftAssignment.UvFilePathFor("cache craft: wings?")), "Delete also removes the saved coordinates");
    }

    // ------------------------------------------------------------------ JSON, glTF / GLB and folders (sets)

    static void Reset()
    {
        string folder = TextureLibrary.Folder;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
    }

    static byte[] Png(byte tag)
    {
        byte[] b = new byte[16];
        b[0] = 0x89; b[1] = (byte)'P'; b[2] = (byte)'N'; b[3] = (byte)'G'; b[8] = tag;
        return b;
    }

    // A glTF with a quad (material 0: airframe, images 0 base, 1 normal, 2 occlusion-metallic-roughness) and a triangle of
    // another material (glass, image 3), on a node translated by (1, 2, 3).
    static byte[] BuildBinary()
    {
        List<byte> bin = new List<byte>();
        foreach (float f in new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }) bin.AddRange(BitConverter.GetBytes(f));
        foreach (float f in new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }) bin.AddRange(BitConverter.GetBytes(f));
        foreach (ushort i in new ushort[] { 0, 1, 2, 0, 2, 3 }) bin.AddRange(BitConverter.GetBytes(i));
        foreach (ushort i in new ushort[] { 0, 1, 2 }) bin.AddRange(BitConverter.GetBytes(i));
        bin.AddRange(new byte[2]); // padding to 100
        bin.AddRange(Png(1)); bin.AddRange(Png(2)); bin.AddRange(Png(3)); bin.AddRange(Png(4));
        return bin.ToArray();
    }

    static string BuildJson(string bufferUri)
    {
        return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],"
            + "\"nodes\":[{\"mesh\":0,\"translation\":[1,2,3]}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":0},"
            + "{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":3,\"material\":1}]}],"
            + "\"materials\":[{\"name\":\"airframe\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":2}},"
            + "\"normalTexture\":{\"index\":1},\"occlusionTexture\":{\"index\":2}},"
            + "{\"name\":\"glass\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":3}}}],"
            + "\"textures\":[{\"source\":0},{\"source\":1},{\"source\":2},{\"source\":3}],"
            + "\"images\":[{\"bufferView\":4,\"mimeType\":\"image/png\"},{\"bufferView\":5},{\"bufferView\":6},{\"bufferView\":7}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"},"
            + "{\"bufferView\":2,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"},{\"bufferView\":3,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48},{\"buffer\":0,\"byteOffset\":48,\"byteLength\":32},{\"buffer\":0,\"byteOffset\":80,\"byteLength\":12},"
            + "{\"buffer\":0,\"byteOffset\":92,\"byteLength\":6},{\"buffer\":0,\"byteOffset\":100,\"byteLength\":16},{\"buffer\":0,\"byteOffset\":116,\"byteLength\":16},"
            + "{\"buffer\":0,\"byteOffset\":132,\"byteLength\":16},{\"buffer\":0,\"byteOffset\":148,\"byteLength\":16}],"
            + "\"buffers\":[{\"byteLength\":164" + (bufferUri != null ? ",\"uri\":\"" + bufferUri + "\"" : "") + "}]}";
    }

    static void WriteGlb(string path)
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes(BuildJson(null));
        int padded = (json.Length + 3) & ~3;
        byte[] bin = BuildBinary();
        List<byte> glb = new List<byte>();
        glb.AddRange(System.Text.Encoding.ASCII.GetBytes("glTF"));
        glb.AddRange(BitConverter.GetBytes(2));
        glb.AddRange(BitConverter.GetBytes(12 + 8 + padded + 8 + bin.Length));
        glb.AddRange(BitConverter.GetBytes(padded));
        glb.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        glb.AddRange(json);
        for (int i = json.Length; i < padded; i++) glb.Add((byte)' ');
        glb.AddRange(BitConverter.GetBytes(bin.Length));
        glb.AddRange(BitConverter.GetBytes(0x004E4942u));
        glb.AddRange(bin);
        File.WriteAllBytes(path, glb.ToArray());
    }

    static void GltfAndSets()
    {
        // JSON
        string error;
        object parsed = Json.Parse("{\"a\":[1,2.5,-3e2,true,null,\"x\\u0041\\n\"],\"b\":{\"c\":false}}", out error);
        Check(parsed != null && Json.Array(parsed, "a").Count == 6 && (double)Json.Array(parsed, "a")[2] == -300 && (string)Json.Array(parsed, "a")[5] == "xA\n"
            && !Json.Bool(Json.Object(parsed, "b"), "c", true), "the JSON reader reads numbers, strings with escapes, nested values");
        Check(Json.Parse("{\"a\":", out error) == null && error != null, "broken JSON is refused with a reason");

        Reset();
        string dir = TextureLibrary.Folder;
        Directory.CreateDirectory(Path.Combine(dir, "glb set"));
        WriteGlb(Path.Combine(dir, "glb set", "test.glb"));

        // The GLB: main material, triangles, texture coordinates, node transform, extraction.
        GltfDocument doc = GltfDocument.Open(Path.Combine(dir, "glb set", "test.glb"), out error);
        Check(doc != null, "a GLB opens (" + error + ")");
        GltfDocument.Primary primary = doc.FindPrimary(out error);
        Check(primary != null && primary.BaseColor == 0 && primary.Triangles == 2 && primary.Normal == 1 && primary.Occlusion == 2 && primary.MetalRough == 2
            && primary.MaterialName == "airframe", "the material that covers most triangles is chosen, with its maps");
        ObjModel model = doc.BuildModel(primary, false, out error);
        Check(model != null && model.TriangleCount == 2, "only the triangles of that material are used (got " + (model != null ? model.TriangleCount : -1) + ")");
        if (model != null)
        {
            Check(model.A[0].x == 1 && model.A[0].y == 2 && model.A[0].z == 3 && model.B[0].x == 2 && model.C[0].y == 3, "the node's translation is applied");
            Check(Math.Abs((model.UvA[0].y - model.UvC[0].y) - 1f) < 1e-5f && Math.Abs((model.UvB[0].x - model.UvA[0].x) - 1f) < 1e-5f, "v is turned over (glTF has it from the top)");
        }

        ObjModel mirrored = doc.BuildModel(primary, true, out error);
        Check(mirrored != null && mirrored.A[0].x == -1 && mirrored.B[0].x == -model.C[0].x, "mirrored on X like the OBJ reader");

        GltfDocument.ExtractedFiles files = doc.Extract(primary, Path.Combine(dir, "_cache", "glb set"), "test", TextureLibrary.RelativeOf, out error);
        Check(files != null && files.BaseColor == "_cache/glb set/test_baseColor.png" && files.Normal == "_cache/glb set/test_normal.png"
            && files.MetalRough == "_cache/glb set/test_occlusion_metallic_roughness.png" && files.Occlusion == files.MetalRough
            && File.Exists(Path.Combine(dir, "_cache", "glb set", "test_baseColor.png")), "the images are written with names that say their role");

        // The same model as .gltf with the buffer inside (base64).
        string gltfPath = Path.Combine(dir, "glb set", "same.gltf");
        File.WriteAllText(gltfPath, BuildJson("data:application/octet-stream;base64," + Convert.ToBase64String(BuildBinary())));
        GltfDocument gltf = GltfDocument.Open(gltfPath, out error);
        GltfDocument.Primary p2 = gltf != null ? gltf.FindPrimary(out error) : null;
        ObjModel m2 = p2 != null ? gltf.BuildModel(p2, false, out error) : null;
        Check(m2 != null && m2.TriangleCount == 2, "a .gltf with an embedded buffer reads the same");

        // Compressed files are refused clearly.
        File.WriteAllText(Path.Combine(dir, "glb set", "draco.gltf"), "{\"asset\":{\"version\":\"2.0\"},\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]}");
        Check(GltfDocument.Open(Path.Combine(dir, "glb set", "draco.gltf"), out error) == null && error.Contains("compression"), "Draco compression is refused with the reason");
        File.Delete(Path.Combine(dir, "glb set", "draco.gltf"));
        File.Delete(gltfPath);

        // Sets: one folder per aircraft.
        Directory.CreateDirectory(Path.Combine(dir, "b2"));
        Directory.CreateDirectory(Path.Combine(dir, "fw 190"));
        foreach (string f in new[] { "B2BaseColor.png", "B2Normal.png", "B2Metallic-B2Roughness@channels=B.png", "B2Metallic-B2Roughness@channels=G.png", "B-2.obj" })
            File.WriteAllText(Path.Combine(dir, "b2", f), "x");
        foreach (string f in new[] { "fw_190d_9_a.jpg", "fw_190d_9_n_n.jpg", "fw_190d_9_n_ao.jpg", "fw_190d_9_n_s.jpg", "fw_190d_9_a.obj" })
            File.WriteAllText(Path.Combine(dir, "fw 190", f), "x");
        Directory.CreateDirectory(Path.Combine(dir, "Crafts"));
        File.WriteAllText(Path.Combine(dir, "Crafts", "x.png"), "x"); // not a set

        Plugin.SelectedSet.Value = "";
        Plugin.SelectedTexture.Value = "";
        Plugin.SelectedModel.Value = "auto";
        foreach (var entry in new[] { Plugin.SelectedNormal, Plugin.SelectedOcclusion, Plugin.SelectedSpecular, Plugin.SelectedMetalness, Plugin.SelectedRoughness, Plugin.SelectedHeight }) entry.Value = "auto";
        TextureLibrary.Refresh();
        Check(TextureLibrary.SetCount == 3 && TextureLibrary.CurrentSetName == "b2", "three sets (b2, fw 190, glb set); Crafts and _cache are not sets (got " + TextureLibrary.SetCount + ", " + TextureLibrary.CurrentSetName + ")");
        Check(TextureLibrary.CurrentImage.Relative == "b2/B2BaseColor.png" && TextureLibrary.CurrentModelRelative == "b2/B-2.obj"
            && TextureLibrary.Normal.CurrentRelative == "b2/B2Normal.png" && TextureLibrary.Metalness.CurrentName == "B2Metallic-B2Roughness@channels=B.png"
            && TextureLibrary.Roughness.CurrentName == "B2Metallic-B2Roughness@channels=G.png", "set b2: model, image and maps found by themselves");

        TextureLibrary.StepSet(1);
        Check(TextureLibrary.CurrentSetName == "fw 190" && TextureLibrary.CurrentImage.Relative == "fw 190/fw_190d_9_a.jpg" && TextureLibrary.CurrentModelName == "fw_190d_9_a.obj"
            && TextureLibrary.Normal.CurrentName == "fw_190d_9_n_n.jpg" && TextureLibrary.Occlusion.CurrentName == "fw_190d_9_n_ao.jpg"
            && TextureLibrary.Specular.CurrentName == "fw_190d_9_n_s.jpg", "set fw 190: everything found by itself");

        TextureLibrary.StepSet(1);
        Check(TextureLibrary.CurrentSetName == "glb set" && TextureLibrary.CurrentModelName == "test.glb"
            && TextureLibrary.CurrentImage.Relative == "_cache/glb set/test_baseColor.png" && TextureLibrary.Normal.CurrentRelative == "_cache/glb set/test_normal.png"
            && TextureLibrary.Metalness.CurrentRelative == "_cache/glb set/test_occlusion_metallic_roughness.png" && TextureLibrary.Roughness.CurrentRelative == TextureLibrary.Metalness.CurrentRelative
            && TextureLibrary.Occlusion.CurrentRelative == TextureLibrary.Metalness.CurrentRelative, "set with a GLB: the images come from its material (" + TextureLibrary.CurrentImage.Relative + ")");

        // A saved craft names paths relative to the folder: the set is found from the model.
        CraftAssignment a = new CraftAssignment();
        a.Image = "fw 190/fw_190d_9_a.jpg"; a.Model = "fw 190/fw_190d_9_a.obj"; a.Normal = "fw 190/fw_190d_9_n_n.jpg";
        string problem;
        Check(TextureLibrary.Select(a, out problem) && TextureLibrary.CurrentSetName == "fw 190" && TextureLibrary.Normal.CurrentName == "fw_190d_9_n_n.jpg",
            "a saved craft brings its set back (" + problem + ")");
        a.Image = "_cache/glb set/test_baseColor.png"; a.Model = "glb set/test.glb"; a.Normal = ""; a.MetallicMap = "_cache/glb set/test_occlusion_metallic_roughness.png";
        Check(TextureLibrary.Select(a, out problem) && TextureLibrary.CurrentSetName == "glb set" && TextureLibrary.CurrentImage.Relative == a.Image
            && TextureLibrary.Metalness.CurrentRelative == a.MetallicMap, "and the images extracted from a GLB (" + problem + ")");
        Check(TextureLibrary.PathFor("fw 190/fw_190d_9_a.jpg") != null && TextureLibrary.PathFor("nothing/x.png") == null, "PathFor reads relative paths");
    }

    // ------------------------------------------------------------------ the OBJ reader

    static void ObjReading()
    {
        string dir = TextureLibrary.Folder;
        string path = Path.Combine(dir, "hand.obj");
        File.WriteAllText(path,
            "# a comment\r\nmtllib x.mtl\r\no cube\r\n"
            + "v 0 0 0\r\nv 1.5 0 0\r\nv 1.5e0 -2.5E-1 +3 1.0\r\nv  .5 5. 0   \r\n"
            + "vt 0.2 0.3\r\nvt 0.8 0.3\r\nvt 0.8 0.7\r\nvt 0.2 0.7\r\nvn 0 0 1\r\n"
            + "f 1/1/1 2/2/1 3/3/1 4/4/1\r\n"      // a quad: two triangles
            + "f 1//1 2//1 3//1\r\n"               // no texture coordinates
            + "f -4/-4 -3/-3 -2/-2\r\n"            // negative indices
            + "f 1 2\r\n"                          // too short: skipped
            + "f 1/1 9/1 3/1\r\n"                  // vertex 9 does not exist: skipped
            + "v 1 2\r\n");                        // too few numbers: skipped
        string error;
        ObjModel m = ObjModel.Load(path, false, out error);
        Check(m != null && error == null, "a hand-made OBJ is read (" + error + ")");
        if (m != null)
        {
            Check(m.TriangleCount == 3, "a quad gives two triangles, the face without texture coordinates is left out, the last one is kept (got " + m.TriangleCount + ")");
            Check(m.A[0].x == 0 && m.B[0].x == 1.5f && m.C[0].x == 1.5f && Math.Abs(m.C[0].y + 0.25f) < 1e-6f && m.C[0].z == 3f, "numbers with exponents and signs");
            Check(m.UvA[0].x == 0.2f && m.UvB[0].x == 0.8f && m.UvC[0].y == 0.7f, "texture coordinates of the first triangle");
            Check(m.A[2].x == 0f && m.B[2].x == 1.5f && m.UvB[2].x == 0.8f, "negative indices count from the end of the list read so far");
        }

        // Mostly no texture coordinates: refused with the numbers; no vt at all: refused; some objects without: left out.
        File.WriteAllText(Path.Combine(dir, "few.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\n"
            + "f 1/1 2/2 3/3\nf 1//1 2//1 3//1\nf 1 2 3\nf 1 2 3\nf 1 2 3\nf 1 2 3\nf 1 2 3\nf 1 2 3\nf 1 2 3\nf 1 2 3\n");
        ObjModel few = ObjModel.Load(Path.Combine(dir, "few.obj"), false, out error);
        Check(few == null && error.Contains("only 1 of 10"), "a file where almost no face has texture coordinates is refused with the numbers (" + error + ")");
        File.WriteAllText(Path.Combine(dir, "novt.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        Check(ObjModel.Load(Path.Combine(dir, "novt.obj"), false, out error) == null && error.Contains("no texture coordinates"), "a file without vt is refused");
        File.WriteAllText(Path.Combine(dir, "half.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\n"
            + "f 1/1 2/2 3/3\nf 1/1 2/2 3/3\nf 1 2 3\nf 1 2 3\n");
        ObjModel half = ObjModel.Load(Path.Combine(dir, "half.obj"), false, out error);
        Check(half != null && half.TriangleCount == 2, "triangles without texture coordinates are left out when enough have them");

        // The same file mirrored on X swaps two corners and negates x.
        ObjModel mirrored = ObjModel.Load(path, true, out error);
        Check(mirrored != null && mirrored.B[0].x == -m.C[0].x && mirrored.C[0].x == -m.B[0].x, "mirrored on X");

        // Random files against the old string-based reader.
        Random rnd = new Random(12345);
        for (int round = 0; round < 5; round++)
        {
            string file = Path.Combine(dir, "random" + round + ".obj");
            string text = RandomObj(rnd, 400 + round * 300, round % 2 == 0 ? "\r\n" : "\n");
            File.WriteAllText(file, text);
            ObjModel fast = ObjModel.Load(file, round > 2, out error);
            List<float[]> reference = ReferenceTriangles(text, round > 2);
            bool same = fast != null && fast.TriangleCount == reference.Count;
            double worst = 0;
            for (int i = 0; same && i < reference.Count; i++)
            {
                float[] r = reference[i];
                float[] f = { fast.A[i].x, fast.A[i].y, fast.A[i].z, fast.B[i].x, fast.B[i].y, fast.B[i].z, fast.C[i].x, fast.C[i].y, fast.C[i].z,
                    fast.UvA[i].x, fast.UvA[i].y, fast.UvB[i].x, fast.UvB[i].y, fast.UvC[i].x, fast.UvC[i].y };
                for (int k = 0; k < 15; k++)
                {
                    double diff = Math.Abs(f[k] - r[k]) / Math.Max(1e-3, Math.Abs(r[k]));
                    worst = Math.Max(worst, diff);
                }
            }

            Check(same && worst < 1e-6, "random OBJ " + round + " matches the old reader (" + (fast != null ? fast.TriangleCount : -1) + " triangles, worst relative difference " + worst.ToString("0.0e+0") + ")");
        }

        // Speed: 400 thousand triangles.
        string big = Path.Combine(dir, "big.obj");
        File.WriteAllText(big, RandomObj(new Random(7), 400000, "\n"));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        ObjModel bigModel = ObjModel.Load(big, true, out error);
        watch.Stop();
        Console.WriteLine("INFO 400000 faces read in " + watch.ElapsedMilliseconds + " ms (" + new FileInfo(big).Length / 1000000 + " MB)");
        Check(bigModel != null && bigModel.TriangleCount >= 400000 && watch.ElapsedMilliseconds < 5000, "a big OBJ is read quickly");
    }

    static string RandomObj(Random rnd, int faces, string eol)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        int vertices = faces / 2 + 10;
        int uvs = faces / 3 + 10;
        sb.Append("# random").Append(eol);
        for (int i = 0; i < vertices; i++)
        {
            string[] formats = { "0.######", "0.0", "0.###e+0", "#.#####" };
            double scale = i % 7 == 0 ? 1000 : (i % 5 == 0 ? 0.001 : 10);
            sb.Append("v ").Append(Num(rnd, scale)).Append(' ').Append(Num(rnd, scale)).Append(' ').Append(Num(rnd, scale)).Append(eol);
            if (i % 11 == 0) sb.Append("vn 0 1 0").Append(eol);
        }

        for (int i = 0; i < uvs; i++)
        {
            sb.Append("vt ").Append((0.1 + rnd.NextDouble() * 0.8).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                .Append((0.1 + rnd.NextDouble() * 0.8).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append(eol);
        }

        for (int f = 0; f < faces; f++)
        {
            int corners = f % 9 == 0 ? 4 : (f % 23 == 0 ? 5 : 3);
            int style = f % 4;
            sb.Append("f");
            for (int c = 0; c < corners; c++)
            {
                int v = 1 + rnd.Next(vertices);
                int t = 1 + rnd.Next(uvs);
                sb.Append(' ');
                if (style == 0) sb.Append(v).Append('/').Append(t).Append("/1");
                else if (style == 1) sb.Append(v).Append('/').Append(t);
                else if (style == 2) sb.Append(v - vertices - 1).Append('/').Append(t - uvs - 1);
                else sb.Append(v).Append('/').Append(t).Append("/").Append(1 + rnd.Next(5));
            }

            sb.Append(eol);
        }

        return sb.ToString();
    }

    static string Num(Random rnd, double scale)
    {
        double value = (rnd.NextDouble() - 0.5) * scale;
        switch (rnd.Next(4))
        {
            case 0: return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            case 1: return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            case 2: return value.ToString("0.#####e+0", System.Globalization.CultureInfo.InvariantCulture);
            default: return value.ToString("#.#########", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // The old reader: split every line, parse with float.TryParse. Returns the triangles as 15 numbers each.
    static List<float[]> ReferenceTriangles(string text, bool flipX)
    {
        List<float[]> positions = new List<float[]>();
        List<float[]> uvs = new List<float[]>();
        List<float[]> result = new List<float[]>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;
            string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (line[0] == 'v' && line[1] == ' ')
                positions.Add(new[] { float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) });
            else if (line[0] == 'v' && line[1] == 't' && line[2] == ' ')
                uvs.Add(new[] { float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) });
            else if (line[0] == 'f' && line[1] == ' ')
            {
                int count = p.Length - 1;
                int[] vi = new int[count];
                int[] ti = new int[count];
                for (int i = 0; i < count; i++)
                {
                    string[] parts = p[i + 1].Split('/');
                    int v = int.Parse(parts[0]);
                    int t = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1]) : 0;
                    vi[i] = v > 0 ? v - 1 : positions.Count + v;
                    ti[i] = t > 0 ? t - 1 : uvs.Count + t;
                }

                for (int i = 1; i < count - 1; i++)
                {
                    float[] pa = positions[vi[0]], pb = positions[vi[i]], pc = positions[vi[i + 1]];
                    float[] ua = uvs[ti[0]], ub = uvs[ti[i]], uc = uvs[ti[i + 1]];
                    if (flipX)
                    {
                        pa = new[] { -pa[0], pa[1], pa[2] }; pb = new[] { -pb[0], pb[1], pb[2] }; pc = new[] { -pc[0], pc[1], pc[2] };
                        float[] tp = pb; pb = pc; pc = tp;
                        float[] tu = ub; ub = uc; uc = tu;
                    }

                    result.Add(new[] { pa[0], pa[1], pa[2], pb[0], pb[1], pb[2], pc[0], pc[1], pc[2], ua[0], ua[1], ub[0], ub[1], uc[0], uc[1] });
                }
            }
        }

        return result;
    }
}
