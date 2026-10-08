using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FpsTune.Wpf.Core;

internal static class NativePowerSettings
{
    internal static Func<string, string, string, int>? ReadOverride { get; set; }
    internal static Func<string, string>? FingerprintOverride { get; set; }
    internal static string RequireActiveGuid()
    {
        var guid = NativeSystem.GetActivePowerSchemeGuid();
        if (!Guid.TryParse(guid, out _)) throw new InvalidOperationException("The active power plan could not be read.");
        return guid!;
    }
    internal static int ReadAc(string plan, string subgroup, string setting)
    {
        if (ReadOverride is not null) return ReadOverride(plan, subgroup, setting);
        var p = Guid.Parse(plan); var g = Guid.Parse(subgroup); var s = Guid.Parse(setting);
        var error = PowerReadACValueIndex(IntPtr.Zero, ref p, ref g, ref s, out var value);
        if (error != 0) throw new Win32Exception((int)error);
        return checked((int)value);
    }
    internal static string Fingerprint(string plan)
    {
        if (FingerprintOverride is not null) return FingerprintOverride(plan);
        var scheme = Guid.Parse(plan);
        var name = new byte[4096]; uint size = (uint)name.Length;
        Check(PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, name, ref size));
        var parts = new List<string> { Encoding.Unicode.GetString(name, 0, checked((int)size)) };
        var groups = Enumerate(scheme, null, 17).Append(Guid.Parse("fea3413e-7e05-4911-9a71-700331f1c294")).Distinct().OrderBy(g => g).ToList();
        foreach (var group in groups)
        {
            foreach (var setting in Enumerate(scheme, group, 18).OrderBy(g => g))
            {
                var g = group; var s = setting;
                Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref g, ref s, out var ac));
                Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref g, ref s, out var dc));
                parts.Add($"{group:D}/{setting:D}/{ac}/{dc}");
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }
    private static IReadOnlyList<Guid> Enumerate(Guid scheme, Guid? subgroup, uint flags)
    {
        var result = new List<Guid>();
        var groupPointer = subgroup.HasValue ? Marshal.AllocHGlobal(16) : IntPtr.Zero;
        try
        {
            if (subgroup.HasValue) Marshal.StructureToPtr(subgroup.Value, groupPointer, false);
            for (uint index = 0; index < 10000; index++)
            {
                var buffer = new byte[16]; uint size = 16;
                var error = PowerEnumerate(IntPtr.Zero, ref scheme, groupPointer, flags, index, buffer, ref size);
                if (error == 259) return result;
                Check(error);
                if (size != 16) throw new InvalidOperationException("Invalid power setting identity.");
                result.Add(new Guid(buffer));
            }
            throw new InvalidOperationException("Power settings enumeration exceeds its limit.");
        }
        finally { if (groupPointer != IntPtr.Zero) Marshal.FreeHGlobal(groupPointer); }
    }
    private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error); }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerEnumerate(IntPtr root, ref Guid scheme, IntPtr subgroup, uint flags, uint index, byte[] buffer, ref uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
}
