using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public static class RegistryHelper
{
    internal sealed record Snapshot(bool Existed, object? Value, RegistryValueKind Kind);
    internal static Func<RegistryHive, string, string, Snapshot>? SnapshotOverride { get; set; }
    internal static Action<RegistryHive, string, string, object, RegistryValueKind>? SetOverride { get; set; }
    internal static Action<RegistryHive, string, string>? DeleteOverride { get; set; }
    internal static Snapshot ReadSnapshot(RegistryHive hive, string path, string name)
    {
        if (SnapshotOverride is not null) return SnapshotOverride(hive, path, name);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new Snapshot(false, null, RegistryValueKind.None);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new InvalidOperationException("The registry value changed while it was being read.");
        return new Snapshot(true, value, key.GetValueKind(name));
    }
    public static object? ReadValue(RegistryHive hive, string path, string name)
    {
        return ReadSnapshot(hive, path, name).Value;
    }

    public static bool ValueExists(RegistryHive hive, string path, string name)
    {
        return ReadSnapshot(hive, path, name).Existed;
    }

    public static void SetValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        if (SetOverride is not null) { SetOverride(hive, path, name, value, kind); return; }
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(path, true);
        key.SetValue(name, value, kind);
    }

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        if (DeleteOverride is not null) { DeleteOverride(hive, path, name); return; }
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }
}
