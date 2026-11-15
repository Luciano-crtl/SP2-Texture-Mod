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
        Check(File.Exists(readme) && File.ReadAllText(readme).Contains("Readme version 3"), "README written");

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
            Check(m.TriangleCount == 4, "a quad gives two triangles, plus two more faces (got " + m.TriangleCount + ")");
            Check(m.A[0].x == 0 && m.B[0].x == 1.5f && m.C[0].x == 1.5f && Math.Abs(m.C[0].y + 0.25f) < 1e-6f && m.C[0].z == 3f, "numbers with exponents and signs");
            Check(m.UvA[0].x == 0.2f && m.UvB[0].x == 0.8f && m.UvC[0].y == 0.7f, "texture coordinates of the first triangle");
            Check(m.UvA[2].x == 0f && m.UvA[2].y == 0f, "a corner without a texture coordinate gets zero");
            Check(m.A[3].x == 0f && m.B[3].x == 1.5f, "negative indices count from the end of the list read so far");
        }

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
