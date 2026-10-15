namespace FpsTune.Wpf.Core;

// The backup ID selects a fixed Windows setting; backup documents cannot choose a target.
internal sealed record PowerOption(string Setting, int Target, string? Group = null, int Maximum = 100)
{
    internal const string ProcessorGroup = "54533251-82be-4824-96c1-47b60b740d00";
    internal static PowerOption For(string id) => id switch
    {
        "cpu-epp-ac" => new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0),
        "cpu-max-ac" => new("bc5038f7-23e0-4960-96da-33abaf5935ec", 100),
        "wifi-performance-ac" => new("12bbebe6-58d6-4636-95bb-3217ef867c1a", 0, "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", 3),
        "disk-awake-ac" => new("6738e2c4-e8a5-4a42-b16a-e040e769756e", 0, "0012ee47-9041-4b5d-9b77-535fba8b1442", int.MaxValue),
        _ => throw new InvalidOperationException("Unknown power setting.")
    };
    internal int Read(string plan)
    {
        var value = NativePowerSettings.ReadAc(plan, Group ?? ProcessorGroup, Setting);
        if (value < 0 || value > Maximum) throw new InvalidOperationException("Invalid power value.");
        return value;
    }
    internal bool Set(string plan, int value)
    {
        if (!Guid.TryParse(plan, out _) || value < 0 || value > Maximum)
            throw new InvalidOperationException("Invalid power target.");
        if (Read(plan) == value) return false;
        var result = NativeSystem.Run("powercfg.exe", "-setacvalueindex", plan, Group ?? ProcessorGroup, Setting,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
        if (string.Equals(NativePowerSettings.RequireActiveGuid(), plan, StringComparison.OrdinalIgnoreCase))
        {
            result = NativeSystem.Run("powercfg.exe", "-setactive", plan);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
        }
        if (Read(plan) != value) throw new InvalidOperationException("Processor power setting did not persist.");
        return true;
    }
}
