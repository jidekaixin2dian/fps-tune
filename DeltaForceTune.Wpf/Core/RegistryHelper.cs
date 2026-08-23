using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Core;

public static class RegistryHelper
{
    public static object? ReadValue(RegistryHive hive, string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)?.OpenSubKey(path);
        return key?.GetValue(name);
    }

    public static bool ValueExists(RegistryHive hive, string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)?.OpenSubKey(path);
        return key?.GetValue(name) != null;
    }

    public static void SetValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(path, true);
        key.SetValue(name, value, kind);
    }

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }
}
