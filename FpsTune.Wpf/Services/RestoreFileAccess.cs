using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FpsTune.Wpf.Services;

/// <summary>Check effective token access for atomic replacement/deletion, without creating or changing files.</summary>
internal static class RestoreFileAccess
{
    internal static bool RequiresElevation(string directory, params string[] files)
    {
        const uint addFile = 0x2, deleteChild = 0x40, delete = 0x10000;
        if (!CanOpen(directory, addFile, isDirectory: true)) return true;
        if (files.Where(f => File.Exists(Path.Combine(directory, f)))
            .All(f => CanOpen(Path.Combine(directory, f), delete))) return false;
        // Windows allows deletion either through the file's DELETE right or its parent's DELETE_CHILD.
        return !CanOpen(directory, deleteChild, isDirectory: true);
    }

    private static bool CanOpen(string path, uint access, bool isDirectory = false)
    {
        using var handle = CreateFile(path, access, 1 | 2 | 4, nint.Zero, 3, isDirectory ? 0x02000000u : 0u, nint.Zero);
        if (!handle.IsInvalid) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == 5) return false;
        // A sharing violation/missing target is not fixed by elevation. Report the actual obstruction.
        throw new IOException(new Win32Exception(error).Message, new Win32Exception(error));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
