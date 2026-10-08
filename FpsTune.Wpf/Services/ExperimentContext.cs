using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record ExperimentContext(string BatchId, string Game, string? GamePath, string Mode,
    int DurationSec, string RecipeHash, string EnvironmentHash, string Scene)
{
    internal static ExperimentContext Create(ExperimentRunner.Options options, string? gamePath, string? batch = null)
    {
        var mode = options.Simulate ? "simulated" : options.CsvPath is not null ? "manual" : "auto";
        var environment = "simulated";
        if (!options.Simulate)
        {
            var hardware = HardwareInfoService.Get();
            var driverKey = NativeSystem.GetMainGpuDriverKeyPath();
            var driver = driverKey is null ? "unknown" : RegistryHelper.ReadValue(Microsoft.Win32.RegistryHive.LocalMachine, driverKey, "DriverVersion")?.ToString();
            environment = FormattableString.Invariant($"{Environment.OSVersion.Version}|{hardware.Cpu}|{hardware.Gpu}|{hardware.RamGB}|{driver}");
        }
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return new(batch ?? Guid.NewGuid().ToString("N"), options.GameName ?? "", gamePath?.ToLowerInvariant(), mode,
            options.DurationSec, Hash(JsonSerializer.Serialize(new
            {
                Build = typeof(ExperimentRunner).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion,
                Items = ItemCatalog.All.Select(i => new { i.Id, i.Admin, i.Reboot, i.Kind, i.Default })
            })), Hash(environment), options.Scene ?? "");
    }
}
