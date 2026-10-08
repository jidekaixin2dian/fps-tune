using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class LicenseTests
{
    [Fact]
    public void Single_executable_exposes_complete_original_license_and_component_notices()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "LICENSE")))
            root = Directory.GetParent(root)?.FullName ?? throw new Exception("Repository not found");
        Assert.Equal(File.ReadAllText(Path.Combine(root, "LICENSE")), LicenseService.ProjectLicense);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt")), LicenseService.ThirdPartyNotices);
        using var output = new StringWriter();
        Assert.Equal(0, CliHost.Run(["-License"], output));
        Assert.Contains("delta-force-tune contributors", output.ToString());
        Assert.Contains("Copyright (c) 2016 Orbmu2k", output.ToString());
        Assert.Contains("THE SOFTWARE IS PROVIDED", output.ToString());
        Assert.Contains("System.CodeDom", output.ToString());
    }
}
