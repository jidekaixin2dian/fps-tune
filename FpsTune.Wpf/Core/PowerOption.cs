namespace FpsTune.Wpf.Core;

// The backup ID selects a fixed Windows setting; backup documents cannot choose a target.
internal sealed record PowerOption(string Setting, int Target)
{
    internal const string ProcessorGroup = "54533251-82be-4824-96c1-47b60b740d00";
    internal static PowerOption For(string id) => id switch
    {
        "cpu-epp-ac" => new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0),
        "cpu-max-ac" => new("bc5038f7-23e0-4960-96da-33abaf5935ec", 100),
        _ => throw new InvalidOperationException("Unknown processor power setting.")
    };
    internal int Read(string plan)
    {
        var value = NativePowerSettings.ReadAc(plan, ProcessorGroup, Setting);
        if (value is < 0 or > 100) throw new InvalidOperationException("Invalid processor power value.");
        return value;
    }
    internal bool Set(string plan, int value)
    {
        if (!Guid.TryParse(plan, out _) || value is < 0 or > 100)
            throw new InvalidOperationException("Invalid processor power target.");
        if (Read(plan) == value) return false;
        var result = NativeSystem.Run("powercfg.exe", "-setacvalueindex", plan, ProcessorGroup, Setting,
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
