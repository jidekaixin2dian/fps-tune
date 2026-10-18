using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FpsTune.Wpf.Services;

public sealed record GpuDriverAdvice(string Product, string Series, string Version, string DriverName,
    DateOnly Released, string DetailsUrl, DateTimeOffset CheckedAt, bool Notebook);

/// <summary>Read NVIDIA's official product catalogue and latest matching WHQL release; never install a driver.</summary>
public static class GpuDriverAdvisor
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string Catalogue = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx";
    private const string Lookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";
    public const string HotfixUrl = "https://www.nvidia.com/en-us/geforce/forums/g-sync-pulsar/131/591809/pulsar-hotfix-driver/";

    public static string? NvidiaSeries(string? gpuName)
    {
        var match = Regex.Match(gpuName ?? "", @"\b(?:RTX|GTX)\s*(10|16|20|30|40|50)\d{2}\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string OfficialUrl(string? gpu) => GpuVendor.Of(gpu) switch
    {
        GpuVendorKind.Amd => "https://www.amd.com/en/support/download/drivers.html",
        GpuVendorKind.Intel => "https://www.intel.com/content/www/us/en/download-center/home.html",
        _ => "https://www.nvidia.com/en-us/drivers/"
    };

    internal static string ProductName(string gpu)
    {
        var name = gpu.Split('·')[0];
        name = Regex.Replace(name, @"\(\+\d+\)", "");
        name = Regex.Replace(name, @"\b(NVIDIA|GeForce|GPU)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\s+", " ").Trim().ToUpperInvariant();
    }

    internal static int OperatingSystemId(int build) => build >= 22000 ? 135 : 57;

    public static async Task<GpuDriverAdvice> QueryAsync(string gpu, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await QueryAsync(gpu, Environment.OSVersion.Version.Build,
            async (url, token) => await Client.GetStringAsync(url, token), timeout.Token);
    }

    internal static async Task<GpuDriverAdvice> QueryAsync(string gpu, int osBuild,
        Func<string, CancellationToken, Task<string>> read, CancellationToken cancellationToken = default)
    {
        if (osBuild < 10240) throw new InvalidOperationException(Str.T("Str.DriverOsUnsupported"));
        var series = NvidiaSeries(gpu) ?? throw new InvalidOperationException(Str.T("Str.DriverModelNotFound"));
        var productName = ProductName(gpu);
        var notebook = productName.Contains("LAPTOP", StringComparison.Ordinal);
        var seriesXml = await read($"{Catalogue}?TypeID=2&ParentID=1", cancellationToken);
        var seriesEntries = Entries(seriesXml).Where(e =>
            Regex.IsMatch(e.Name, $@"\b{series}\s+Series\b", RegexOptions.IgnoreCase)
            && e.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == notebook).ToArray();
        if (seriesEntries.Length != 1) throw new InvalidOperationException(Str.T("Str.DriverModelNotFound"));
        var seriesId = seriesEntries[0].Id;
        var productsXml = await read($"{Catalogue}?TypeID=3&ParentID={seriesId}", cancellationToken);
        var products = Entries(productsXml).Where(e => ProductName(e.Name) == productName).ToArray();
        if (products.Length != 1) throw new InvalidOperationException(Str.T("Str.DriverModelNotFound"));
        var url = $"{Lookup}?func=DriverManualLookup&psid={seriesId}&pfid={products[0].Id}"
            + $"&osID={OperatingSystemId(osBuild)}&languageCode=1033&isWHQL=1&beta=0&dch=1&dltype=1&sort1=0&numberOfResults=1";
        return ParseRelease(await read(url, cancellationToken), products[0].Name, series, notebook, DateTimeOffset.Now);
    }

    private static IEnumerable<(string Name, int Id)> Entries(string xml)
        => XDocument.Parse(xml).Descendants("LookupValue")
            .Select(e => (Name: e.Element("Name")?.Value ?? "", Id: int.TryParse(e.Element("Value")?.Value, out var id) ? id : 0))
            .Where(e => e.Id > 0);

    internal static GpuDriverAdvice ParseRelease(string json, string product, string series, bool notebook, DateTimeOffset checkedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("Success").GetString() != "1") throw new InvalidOperationException(Str.T("Str.DriverNoRelease"));
        var info = root.GetProperty("IDS").EnumerateArray().Single().GetProperty("downloadInfo");
        string Field(string key) => info.GetProperty(key).GetString() ?? "";
        var version = Field("Version");
        var name = Uri.UnescapeDataString(Field("Name"));
        var details = Field("DetailsURL");
        if (Field("Success") != "1" || Field("IsWHQL") != "1" || Field("IsBeta") != "0"
            || !Regex.IsMatch(version, @"^\d{3}\.\d{2}$")
            || !name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Studio", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(details, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "www.nvidia.com"
            || !Regex.IsMatch(uri.AbsolutePath, @"^/en-us/(geforce/)?drivers/details/\d+/$")
            || !DateOnly.TryParseExact(Field("ReleaseDateTime"), ["ddd MMM dd, yyyy", "ddd MMM d, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var released)
            || released > DateOnly.FromDateTime(checkedAt.UtcDateTime))
            throw new InvalidOperationException(Str.T("Str.DriverInvalidResponse"));
        return new(product, series, version, name, released, details, checkedAt, notebook);
    }

    // A dated, scoped supplement from the official staff post; never substitute a hotfix for the live WHQL recommendation.
    internal static bool ShowHotfix(GpuDriverAdvice advice, DateOnly today)
        => advice.Version == "617.42" && advice.Series is "20" or "30" or "40" or "50"
            && today >= new DateOnly(2026, 10, 18) && today <= new DateOnly(2026, 11, 17);
}
