using System.IO;

namespace FpsTune.Wpf.Services;

internal static class NvidiaDrsAccess
{
    internal static string DatabaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NVIDIA Corporation", "Drs");

    internal static void SaveSettings(Action save, string directory, bool isAdministrator)
    {
        EnsureDatabaseDirectory(directory, isAdministrator);
        save();
    }

    // Some driver installations omit Drs; SaveSettings then reports ACCESS_DENIED,
    // including for elevated processes. Never delete databases or change their ACLs.
    internal static void EnsureDatabaseDirectory(string directory, bool isAdministrator)
    {
        if (Directory.Exists(directory)) return;
        if (!isAdministrator)
            throw new NvdrsException(-175, Str.T("Str.NvDrsDirectoryNeedsAdmin", directory));
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NvdrsException(-175, Str.T("Str.NvDrsDirectoryFailed", directory, ex.Message), ex);
        }
    }

    internal sealed record Feedback(bool OfferElevation, string TitleKey, string StatusKey, string MessageKey);

    internal static Feedback DescribeAccessDenied(bool isAdministrator, bool restoring) => new(
        !isAdministrator,
        isAdministrator ? "Str.NvDrsAccessDeniedTitle" : "Str.NeedsAdmin",
        restoring ? "Str.NvDrsRestoreDenied" : "Str.NvDrsApplyDenied",
        isAdministrator ? "Str.NvDrsDeniedElevated" : "Str.NvDrsDeniedStandard");
}
