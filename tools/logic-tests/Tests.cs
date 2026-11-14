using System;
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
        Check(File.Exists(readme) && File.ReadAllText(readme).Contains("Readme version 2"), "README written");

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
}
