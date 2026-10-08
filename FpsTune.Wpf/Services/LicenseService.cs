using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>Embedded full texts remain accessible when only FpsTune.exe is distributed.</summary>
public static class LicenseService
{
    public static string ProjectLicense => Read("LICENSE");
    public static string ThirdPartyNotices => Read("THIRD-PARTY-NOTICES");

    private static string Read(string name)
    {
        using var stream = typeof(LicenseService).Assembly.GetManifestResourceStream("FpsTune.Legal." + name)
            ?? throw new InvalidDataException("Missing embedded license: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
