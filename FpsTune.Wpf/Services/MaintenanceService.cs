using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>On-demand local snapshots and fixed Windows maintenance entry points.</summary>
internal static class MaintenanceService
{
    internal sealed record DriveSnapshot(string Name, long Total, long Available)
    {
        public double UsedPercent => Total <= 0 ? 0 : 100d * (Total - Math.Clamp(Available, 0, Total)) / Total;
        public bool LowSpace => Total > 0 && Available / (double)Total < 0.1;
    }
    internal sealed record ProcessSnapshot(string Name, int Pid, long WorkingSet);
    internal sealed record Snapshot<T>(IReadOnlyList<T> Items, int Unavailable);

    internal static Snapshot<DriveSnapshot> ReadDrives()
    {
        var items = new List<DriveSnapshot>();
        var unavailable = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Network shares and removable media may wait for a remote or sleeping device.
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                if (!drive.IsReady) { unavailable++; continue; }
                items.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable++; }
        }
        return new(items, unavailable);
    }

    internal static Snapshot<ProcessSnapshot> ReadProcesses()
    {
        var items = new List<ProcessSnapshot>();
        var unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var bytes = process.WorkingSet64;
                    if (bytes > 0) items.Add(new(process.ProcessName, process.Id, bytes));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                { unavailable++; }
            }
        }
        return new(items.OrderByDescending(p => p.WorkingSet).ThenBy(p => p.Pid).Take(8).ToArray(), unavailable);
    }

    // IDs come from our controls, never from imported settings or command-line text.
    internal static ProcessStartInfo CreateLaunch(string id, string systemDirectory)
    {
        var uri = id switch
        {
            "temporary" => "ms-settings:storagesense",
            "apps" => "ms-settings:appsfeatures",
            "startup" => "ms-settings:startupapps",
            "update" => "ms-settings:windowsupdate",
            "network" => "ms-settings:network-status",
            _ => null
        };
        if (uri is not null) return new(uri) { UseShellExecute = true };
        var (file, argument) = id switch
        {
            "reliability" => ("perfmon.exe", "/rel"),
            "resources" => ("perfmon.exe", "/res"),
            "taskmanager" => ("Taskmgr.exe", ""),
            _ => throw new ArgumentException("Unknown maintenance tool.", nameof(id))
        };
        if (!Path.IsPathFullyQualified(systemDirectory)) throw new ArgumentException("A full system path is required.", nameof(systemDirectory));
        return new(Path.Combine(systemDirectory, file), argument) { UseShellExecute = true };
    }

    internal static void Open(string id)
    {
        var start = CreateLaunch(id, Environment.SystemDirectory);
        if (!start.FileName.StartsWith("ms-settings:", StringComparison.Ordinal) && !File.Exists(start.FileName))
            throw new FileNotFoundException(Str.T("Str.MaintenanceToolUnavailable"));
        Process.Start(start);
    }
}
