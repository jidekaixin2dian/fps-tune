using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class NvidiaDrsAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-drs-storage-" + Guid.NewGuid().ToString("N"));
    private string DatabaseDirectory => Path.Combine(_root, "NVIDIA Corporation", "Drs");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Elevated_save_repairs_missing_database_directory_before_calling_driver()
    {
        var calls = 0;
        NvidiaDrsAccess.SaveSettings(() =>
        {
            calls++;
            // This is the documented NVIDIA save failure when Drs is absent.
            if (!Directory.Exists(DatabaseDirectory)) throw new NvdrsException(-175, "NVAPI_ACCESS_DENIED");
        }, DatabaseDirectory, isAdministrator: true);
        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(DatabaseDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(DatabaseDirectory));
    }

    [Fact]
    public void Standard_user_does_not_create_a_missing_machine_directory_or_call_save()
    {
        var calls = 0;
        var ex = Assert.Throws<NvdrsException>(() => NvidiaDrsAccess.SaveSettings(
            () => calls++, DatabaseDirectory, isAdministrator: false));
        Assert.Equal(-175, ex.Status);
        Assert.False(Directory.Exists(_root));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_database_is_preserved_and_save_errors_are_not_retried(bool admin)
    {
        Directory.CreateDirectory(DatabaseDirectory);
        var database = Path.Combine(DatabaseDirectory, "nvdrsdb0.bin");
        byte[] original = [0, 17, 255, 83];
        File.WriteAllBytes(database, original);
        var calls = 0;
        var denied = new NvdrsException(-175, "DRS_SaveSettings: NVAPI_ACCESS_DENIED");
        var ex = Assert.Throws<NvdrsException>(() => NvidiaDrsAccess.SaveSettings(
            () => { calls++; throw denied; }, DatabaseDirectory, admin));
        Assert.Same(denied, ex);
        Assert.Equal(1, calls);
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Fact]
    public void Directory_creation_failure_is_preserved_and_never_overwrites_a_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseDirectory)!);
        File.WriteAllText(DatabaseDirectory, "existing file");
        var calls = 0;
        var ex = Assert.Throws<NvdrsException>(() => NvidiaDrsAccess.SaveSettings(
            () => calls++, DatabaseDirectory, isAdministrator: true));
        Assert.Equal(-175, ex.Status);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Equal("existing file", File.ReadAllText(DatabaseDirectory));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true, false, "Str.NvDrsApplyDenied")]
    [InlineData(true, true, "Str.NvDrsRestoreDenied")]
    [InlineData(false, false, "Str.NvDrsApplyDenied")]
    [InlineData(false, true, "Str.NvDrsRestoreDenied")]
    public void Only_standard_users_are_offered_elevation(bool admin, bool restoring, string statusKey)
    {
        var feedback = NvidiaDrsAccess.DescribeAccessDenied(admin, restoring);
        Assert.Equal(!admin, feedback.OfferElevation);
        Assert.Equal(statusKey, feedback.StatusKey);
        Assert.Equal(admin ? "Str.NvDrsAccessDeniedTitle" : "Str.NeedsAdmin", feedback.TitleKey);
        Assert.Equal(admin ? "Str.NvDrsDeniedElevated" : "Str.NvDrsDeniedStandard", feedback.MessageKey);
    }
}
