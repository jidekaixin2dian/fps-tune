using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>One root for app state; verification can isolate every module before loading WPF.</summary>
internal static class UserDataPaths
{
    internal static string? RootOverride { get; set; }
    internal static string Root => RootOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune")
        : Path.GetFullPath(RootOverride);
}
