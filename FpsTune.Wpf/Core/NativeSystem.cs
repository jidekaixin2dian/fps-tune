using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed record NativeResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

internal enum DynamicTickState
{
    Absent,
    No,
    Yes
}

/// <summary>
/// 直接调用 Windows 原生命令的轻量封装，避免为了核心改动再回退 PowerShell。
/// </summary>
internal static class NativeSystem
{
    public static NativeResult Run(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new NativeResult(-1, "", "无法启动 " + fileName);

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new NativeResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new NativeResult(-1, "", ex.Message);
        }
    }

    public static string? GetActivePowerSchemeGuid()
    {
        var r = Run("powercfg.exe", "-getactivescheme");
        if (!r.Success)
            return null;

        var match = Regex.Match(r.Output,
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static int? GetServiceStartValue(string serviceName)
    {
        var value = RegistryHelper.ReadValue(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\" + serviceName,
            "Start");
        if (value is int i)
            return i;
        if (value is long l)
            return (int)l;
        return null;
    }

    public static string? GetServiceStartMode(string serviceName)
    {
        var value = GetServiceStartValue(serviceName);
        return value switch
        {
            0 => "boot",
            1 => "system",
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => null
        };
    }

    public static bool IsHibernateEnabled()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            return File.Exists(Path.Combine(root, "hiberfil.sys"));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDynamicTickEnabled()
    {
        try
        {
            return GetDynamicTickState() == DynamicTickState.Yes;
        }
        catch
        {
            // 检测接口保持布尔兼容；需要修改/备份时由调用方显式报告查询失败。
            return false;
        }
    }

    public static DynamicTickState GetDynamicTickState()
    {
        var r = Run("bcdedit.exe", "/enum", "{current}");
        if (!r.Success)
            throw new InvalidOperationException("查询 disabledynamictick 失败：" + (string.IsNullOrWhiteSpace(r.Error) ? $"退出码 {r.ExitCode}" : r.Error.Trim()));

        return ParseDynamicTickState(r.Output);
    }

    internal static DynamicTickState ParseDynamicTickState(string output)
    {
        var match = Regex.Match(output ?? "", @"(?im)^\s*disabledynamictick\s+(yes|no)\b");
        if (!match.Success)
            return DynamicTickState.Absent;

        return string.Equals(match.Groups[1].Value, "yes", StringComparison.OrdinalIgnoreCase)
            ? DynamicTickState.Yes
            : DynamicTickState.No;
    }

    public static string? GetMainGpuDriverKeyPath()
    {
        const string basePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(basePath);
        if (key is null)
            return null;

        var subs = key.GetSubKeyNames()
            .Where(x => Regex.IsMatch(x, @"^00\d\d$"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (subs.Length == 0)
            return null;

        // 优先选择独立显卡（NVIDIA/AMD/Radeon），再退回 Intel/第一个驱动项；
        // 锁定独立显卡性能状态对帧率优化更有意义。
        foreach (var sub in subs)
        {
            using var subKey = key.OpenSubKey(sub);
            var desc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
            if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                return $@"{basePath}\{sub}";
            }
        }

        foreach (var sub in subs)
        {
            using var subKey = key.OpenSubKey(sub);
            var desc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
            if (desc.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                return $@"{basePath}\{sub}";
        }

        return $@"{basePath}\{subs[0]}";
    }
}
