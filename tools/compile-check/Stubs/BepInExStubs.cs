using System;
using UnityEngine;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version) { }
    }

    public class BaseUnityPlugin : MonoBehaviour
    {
        public BepInEx.Logging.ManualLogSource Logger { get; }
        public BepInEx.Configuration.ConfigFile Config { get; }
    }
}

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
        public void LogDebug(object data) { }
    }
}

namespace BepInEx.Configuration
{
    public abstract class AcceptableValueBase { }

    public class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
    {
        public AcceptableValueRange(T minValue, T maxValue) { }
    }

    public class ConfigDescription
    {
        public ConfigDescription(string description, AcceptableValueBase acceptableValues = null, params object[] tags) { }
    }

    public class ConfigEntry<T>
    {
        public T Value { get; set; }
        public ConfigFile ConfigFile { get; }
    }

    public class ConfigFile
    {
        public bool SaveOnConfigSet { get; set; }
        public void Save() { }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) { return null; }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description = null) { return null; }
    }
}

namespace UnityEngine.InputSystem
{
    public enum Key { None = 0, Insert = 1 }

    public class KeyControl
    {
        public bool wasPressedThisFrame { get; }
    }

    public class Keyboard
    {
        public static Keyboard current { get; }
        public KeyControl this[Key key] { get { return null; } }
    }
}

namespace SP2TextureMod.UI
{
    internal static class TexturePanel
    {
        public static void Tick() { }
        public static void Toggle() { }
    }
}
