using System.IO;
using System.Runtime.CompilerServices;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Tests;

internal static class IsolatedUserData
{
    [ModuleInitializer]
    internal static void UseTestDirectory()
    {
        UserDataPaths.TestFallbackRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../work/test-user-data", Guid.NewGuid().ToString("N")));
    }
}
