namespace FpsTune.Wpf.Core;

internal static class WindowsVersionHelper
{
    private const int Windows11Build = 22000;

    internal static bool? IsWindows11Build(string? currentBuild, string? currentBuildNumber)
    {
        var hasBuild = false;
        foreach (var raw in new[] { currentBuild, currentBuildNumber })
        {
            if (!int.TryParse(raw, out var build))
                continue;

            hasBuild = true;
            if (build >= Windows11Build)
                return true;
        }

        return hasBuild ? false : null;
    }
}
