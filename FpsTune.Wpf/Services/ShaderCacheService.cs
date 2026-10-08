using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>Only named caches under the current user's LocalAppData; preview pins file identity.
/// Cleanup deletes by verified exclusive handle, never by an unchecked path or recursive directory delete.</summary>
internal sealed class ShaderCacheService
{
    private const int MaxFiles = 20000;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly string _base;
    private readonly string[] _roots;
    private static readonly string[] RelativeRoots = ["D3DSCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"NVIDIA Corporation\NV_Cache"];

    internal sealed record Entry(string Path, string Root, long Bytes, long WriteTime, uint Volume, ulong FileId);
    internal sealed record Folder(string Path, int Files, long Bytes);
    internal sealed record Preview(Guid Owner, IReadOnlyList<Entry> Entries, IReadOnlyList<Folder> Folders, IReadOnlyList<string> Skipped, bool Truncated)
    {
        public long Bytes => Entries.Sum(e => e.Bytes);
    }
    internal sealed record Result(int Deleted, long Bytes, IReadOnlyList<string> Skipped);

    internal ShaderCacheService(string localAppData)
    {
        _base = Path.GetFullPath(localAppData).TrimEnd(Path.DirectorySeparatorChar);
        if (_base.Length <= 3 || _base.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("A local user directory is required.");
        _roots = RelativeRoots.Select(r => Path.Combine(_base, r)).ToArray();
    }
    internal static ShaderCacheService ForCurrentUser() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal Preview Scan()
    {
        var files = new List<Entry>();
        var skipped = new List<string>();
        var folders = new List<Folder>();
        var truncated = false;
        var inspected = 0;
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            var first = files.Count;
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var (directory, depth) = pending.Pop();
                try
                {
                    RequireSafePath(directory, root);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++inspected > MaxFiles) { truncated = true; break; }
                        try
                        {
                            var attrs = File.GetAttributes(path);
                            if ((attrs & FileAttributes.ReparsePoint) != 0) { skipped.Add(path); continue; }
                            if ((attrs & FileAttributes.Directory) != 0)
                            {
                                if (depth < 4) pending.Push((path, depth + 1)); else skipped.Add(path);
                                continue;
                            }
                            RequireSafePath(path, root);
                            using var handle = Open(path, deleting: false);
                            files.Add(ReadEntry(handle, path, root));
                        }
                        catch (Exception ex) when (IsFileFailure(ex)) { skipped.Add(path); }
                    }
                }
                catch (Exception ex) when (IsFileFailure(ex)) { skipped.Add(directory); }
                if (truncated) break;
            }
            folders.Add(new(root, files.Count - first, files.Skip(first).Sum(f => f.Bytes)));
            if (truncated) break;
        }
        return new(_owner, files.AsReadOnly(), folders.AsReadOnly(), skipped.AsReadOnly(), truncated);
    }

    internal Result Clean(Preview preview, CancellationToken token = default)
    {
        if (preview.Owner != _owner || preview.Truncated) throw new InvalidOperationException("Scan again before cleaning.");
        using var gate = SystemMutationGate.Acquire();
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning) throw new InvalidOperationException(Str.T("Str.CacheBusy"));
        var skipped = new List<string>();
        var deleted = 0; long bytes = 0;
        foreach (var entry in preview.Entries)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RequireSafePath(entry.Path, entry.Root);
                using var handle = Open(entry.Path, deleting: true); // Exclusive; an open game/driver handle causes a skip.
                if (ReadEntry(handle, entry.Path, entry.Root) != entry) throw new IOException("Cache changed since preview.");
                byte delete = 1; // FILE_DISPOSITION_INFO uses a one-byte BOOLEAN.
                if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                deleted++; bytes += entry.Bytes;
            }
            catch (Exception ex) when (IsFileFailure(ex)) { skipped.Add(entry.Path); }
        }
        return new(deleted, bytes, skipped.AsReadOnly());
    }

    private void RequireSafePath(string path, string root)
    {
        if (!_roots.Contains(root, StringComparer.OrdinalIgnoreCase)) throw new IOException("Unknown cache root.");
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Outside cache root.");
        // Include parent directories up to the volume root, so an existing junction cannot redirect a scan.
        for (var item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked cache paths are skipped.");
    }
    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception;

    private static SafeFileHandle Open(string path, bool deleting)
    {
        var handle = CreateFile(path, 0x80u | (deleting ? 0x10000u : 0), deleting ? 0u : 7u, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    private static Entry ReadEntry(SafeFileHandle handle, string path, string root)
    {
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(Path.GetFullPath(path), resolved, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cache path was redirected.");
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (0x10u | 0x400u | 0x1u)) != 0 || info.Links != 1) throw new IOException("Not an ordinary writable cache file.");
        return new(path, root, ((long)info.SizeHigh << 32) | info.SizeLow,
            ((long)info.WriteHigh << 32) | info.WriteLow, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint bytes);
}
