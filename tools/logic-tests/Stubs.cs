using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0, 0, 0); } }
        public float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public static Vector3 Min(Vector3 a, Vector3 b) { return new Vector3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z)); }
        public static Vector3 Max(Vector3 a, Vector3 b) { return new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z)); }
        public static bool operator ==(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        public static bool operator !=(Vector3 a, Vector3 b) { return !(a == b); }
        public override bool Equals(object o) { return o is Vector3 v && this == v; }
        public override int GetHashCode() { return x.GetHashCode(); }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return new Vector2(0, 0); } }
        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 Min(Vector2 a, Vector2 b) { return new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y)); }
        public static Vector2 Max(Vector2 a, Vector2 b) { return new Vector2(Math.Max(a.x, b.x), Math.Max(a.y, b.y)); }
    }

    public class Mesh
    {
        public System.Collections.Generic.List<Vector3> V = new System.Collections.Generic.List<Vector3>();
        public System.Collections.Generic.List<Vector2> U = new System.Collections.Generic.List<Vector2>();
        public int vertexCount { get { return V.Count; } }
        public void GetVertices(System.Collections.Generic.List<Vector3> list) { list.Clear(); list.AddRange(V); }
        public void SetUVs(int channel, Vector2[] uvs) { U = new System.Collections.Generic.List<Vector2>(uvs); }
        public void GetUVs(int channel, System.Collections.Generic.List<Vector2> list) { list.Clear(); list.AddRange(U); }
    }

    public struct Bounds
    {
        public Vector3 min, max;
        public void SetMinMax(Vector3 a, Vector3 b) { min = a; max = b; }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float sqrMagnitude { get { return x * x + y * y + z * z + w * w; } }
    }
    public struct Matrix4x4
    {
        private float[] m;
        private float[] M { get { if (m == null) m = new float[16]; return m; } }
        public float this[int i] { get { return M[i]; } set { M[i] = value; } }
        public static Matrix4x4 identity { get { Matrix4x4 r = new Matrix4x4(); r[0] = 1; r[5] = 1; r[10] = 1; r[15] = 1; return r; } }
        public Vector4 GetColumn(int c) { return new Vector4(M[c * 4], M[c * 4 + 1], M[c * 4 + 2], M[c * 4 + 3]); }
    }
    public static class Mathf
    {
        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Floor(float v) { return (float)Math.Floor(v); }
    }
    public static class Application
    {
        public static string persistentDataPath = "";
        public static void OpenURL(string url) { }
    }
    public class Object { public static void Destroy(Object o) { } }
    public class Texture2D : Object
    {
        public string name; public FilterMode filterMode; public int anisoLevel;
        public Texture2D(int w, int h, TextureFormat f, bool mip) { }
        public void Apply(bool a, bool b) { }
    }
    public enum TextureFormat { RGBA32 }
    public enum FilterMode { Trilinear }
    public static class ImageConversion { public static bool LoadImage(Texture2D t, byte[] b, bool nonReadable) { return true; } }
}

namespace BepInEx.Configuration
{
    public class ConfigFile { public bool SaveOnConfigSet { get; set; } = true; }
    public class ConfigEntry<T> { public T Value { get; set; } public ConfigFile ConfigFile { get { return null; } } public ConfigEntry(T v) { Value = v; } }
}

namespace SP2TextureMod
{
    using BepInEx.Configuration;
    internal static class Plugin
    {
        public sealed class Logger { public void LogWarning(object o) { Console.WriteLine("WARN " + o); } public void LogInfo(object o) { Console.WriteLine("INFO " + o); } }
        public static readonly Logger Log = new Logger();
        public static ConfigEntry<string> SelectedTexture = new ConfigEntry<string>("");
        public static ConfigEntry<string> SelectedModel = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedNormal = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedOcclusion = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedSpecular = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedMetalness = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedRoughness = new ConfigEntry<string>("auto");
        public static ConfigEntry<string> SelectedHeight = new ConfigEntry<string>("auto");
        public static ConfigEntry<float> TilesPerMeter = new ConfigEntry<float>(1f), RayStart = new ConfigEntry<float>(0f), FacingWeight = new ConfigEntry<float>(0f),
            MaxMatchDistance = new ConfigEntry<float>(0f), AdjustOffsetX = new ConfigEntry<float>(0f), AdjustOffsetY = new ConfigEntry<float>(0f), AdjustOffsetZ = new ConfigEntry<float>(0f),
            AdjustRotationX = new ConfigEntry<float>(0f), AdjustRotationY = new ConfigEntry<float>(0f), AdjustRotationZ = new ConfigEntry<float>(0f), AdjustScale = new ConfigEntry<float>(1f),
            NormalStrength = new ConfigEntry<float>(1f), BumpStrength = new ConfigEntry<float>(3f), OcclusionStrength = new ConfigEntry<float>(1f), ParallaxHeight = new ConfigEntry<float>(0f),
            Metallic = new ConfigEntry<float>(0f), Smoothness = new ConfigEntry<float>(0f), SpecularGain = new ConfigEntry<float>(2f);
        public static ConfigEntry<bool> FlipModelX = new ConfigEntry<bool>(true), ProjectAlongNormals = new ConfigEntry<bool>(true), FlipNormalGreen = new ConfigEntry<bool>(false),
            RebuildNormalZ = new ConfigEntry<bool>(true), HeightToNormal = new ConfigEntry<bool>(false), UseParallax = new ConfigEntry<bool>(false), UseLitShader = new ConfigEntry<bool>(false);
        public static void Set<T>(ConfigEntry<T> entry, T value) { entry.Value = value; }
        public static void SaveConfig() { }
    }
}
