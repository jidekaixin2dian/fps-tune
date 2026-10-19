using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record AudioOutput(string Id, string Name) { public override string ToString() => Name; }
public sealed record EqualizerSettings(string DeviceId, double[] Gains, double Preamp = 0, bool Enabled = true)
{
    public static readonly double[] Frequencies = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public void Validate()
    {
        if (!Guid.TryParse(DeviceId, out _) || Gains is null || Gains.Length != Frequencies.Length
            || Gains.Any(v => !double.IsFinite(v) || v is < -12 or > 12) || !double.IsFinite(Preamp) || Preamp is < -24 or > 0)
            throw new InvalidDataException(Str.T("Str.EqInvalid"));
    }
    public double EffectivePreamp => Math.Min(Preamp, -Math.Max(0, Gains.Max()));
}
public sealed record EqualizerEngine(string InstallPath, string ConfigPath, string DeviceTool);

/// <summary>Use Equalizer APO's documented config language. Own one include, never replace another equalizer's settings.</summary>
public static class AudioEqualizerService
{
    internal static EqualizerEngine? EngineOverride;
    internal static string? BackupDirOverride;
    private static string ReceiptPath => Path.Combine(BackupDirOverride ?? Path.Combine(UserDataPaths.Root, "audio-equalizer"), "receipt.json");
    private const string IncludeName = "FpsTune-Equalizer.txt";
    private const string IncludeBlock = "# BEGIN FpsTune Equalizer\r\nInclude: FpsTune-Equalizer.txt\r\n# END FpsTune Equalizer\r\n";
    private sealed record Receipt(string ConfigPath, byte[] OriginalMain, byte[] Prefix, string? ContentHash, EqualizerSettings Settings, string? PreviousContentHash = null);
    public static EqualizerEngine? FindEngine()
    {
        if (EngineOverride is not null) return EngineOverride;
        using var reg = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        var root = Environment.ExpandEnvironmentVariables(reg?.GetValue("InstallPath") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO"));
        var config = Environment.ExpandEnvironmentVariables(reg?.GetValue("ConfigPath") as string ?? Path.Combine(root, "config"));
        var tool = new[] { "DeviceSelector.exe", "Configurator.exe" }.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
        return tool is null || !File.Exists(Path.Combine(config, "config.txt")) ? null : new(root, config, tool);
    }
    public static IReadOnlyList<AudioOutput> Outputs()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
        var result = new List<AudioOutput>();
        foreach (var id in root?.GetSubKeyNames() ?? [])
        {
            using var endpoint = root!.OpenSubKey(id);
            if (endpoint?.GetValue("DeviceState") is not int state || state != 1 || !Guid.TryParse(id, out _)) continue;
            using var properties = endpoint.OpenSubKey("Properties");
            var name = properties?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14") as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                var connection = properties?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                var device = properties?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                name = string.IsNullOrWhiteSpace(connection) ? device : string.IsNullOrWhiteSpace(device) ? connection : connection + " (" + device + ")";
            }
            result.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : name));
        }
        return result;
    }
    internal static string BuildConfiguration(EqualizerSettings settings)
    {
        settings.Validate();
        static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var text = "# Managed by FPS Tune. Existing config.txt remains intact.\n";
        if (!settings.Enabled) return text + "# Bypass\n";
        // Device selector scope ends inside this include. Capture devices are never selected.
        return text + "Device: " + Guid.Parse(settings.DeviceId).ToString("B") + "\nChannel: all\nPreamp: " + Number(settings.EffectivePreamp)
            + " dB\nGraphicEQ: " + string.Join("; ", EqualizerSettings.Frequencies.Select((f, i) => Number(f) + " " + Number(settings.Gains[i]))) + "\nDevice: all\nChannel: all\n";
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Receipt? Load()
    {
        if (!File.Exists(ReceiptPath)) return null;
        if (new FileInfo(ReceiptPath).Length > 3000000) throw new InvalidDataException(Str.T("Str.EqBackupLarge"));
        var r = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(ReceiptPath)) ?? throw new InvalidDataException(Str.T("Str.EqBackupInvalid"));
        if (r.OriginalMain is null || r.Prefix is null || r.Prefix.Length == 0 || r.Settings is null) throw new InvalidDataException(Str.T("Str.EqBackupInvalid"));
        r.Settings.Validate();
        return r;
    }
    private static void Save(Receipt r) => AtomicFile.WriteAllTextDurable(ReceiptPath, JsonSerializer.Serialize(r), new UTF8Encoding(false));
    public static EqualizerSettings? SavedSettings() => Load()?.Settings;
    public static bool HasBackup => File.Exists(ReceiptPath);
    private static Encoding EncodingFor(byte[] bytes) => bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()) ? Encoding.Unicode
        : bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()) ? Encoding.BigEndianUnicode : new UTF8Encoding(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    private static int PreambleLength(byte[] bytes, Encoding encoding) => bytes.AsSpan().StartsWith(encoding.GetPreamble()) ? encoding.GetPreamble().Length : 0;
    private static void CheckOwnFile(string path, Receipt r)
    {
        if (File.Exists(path) && Hash(File.ReadAllBytes(path)) is var hash && hash != r.ContentHash && hash != r.PreviousContentHash)
            throw new InvalidOperationException(Str.T("Str.EqFileChanged"));
    }
    private static int FindEntry(byte[] bytes, Receipt r)
    {
        var index = bytes.AsSpan().IndexOf(r.Prefix);
        var encoding = EncodingFor(bytes);
        var newline = encoding.GetBytes("\n");
        if (index >= 0 && index != PreambleLength(bytes, encoding)
            && (index < newline.Length || !bytes.AsSpan(index - newline.Length, newline.Length).SequenceEqual(newline)))
            throw new InvalidOperationException(Str.T("Str.EqEntryAmbiguous"));
        if (index >= 0 && bytes.AsSpan(index + r.Prefix.Length).IndexOf(r.Prefix) >= 0)
            throw new InvalidOperationException(Str.T("Str.EqEntryAmbiguous"));
        return index;
    }
    public static void Apply(EqualizerSettings settings)
    {
        settings.Validate();
        using var gate = SystemMutationGate.Acquire();
        var engine = FindEngine() ?? throw new InvalidOperationException(Str.T("Str.EqInstallFirst"));
        if (EngineOverride is null && !Outputs().Any(d => Guid.Parse(d.Id) == Guid.Parse(settings.DeviceId)))
            throw new InvalidOperationException(Str.T("Str.EqDisconnected"));
        var mainPath = Path.Combine(engine.ConfigPath, "config.txt");
        var ownPath = Path.Combine(engine.ConfigPath, IncludeName);
        if (new FileInfo(mainPath).Length > 1000000) throw new InvalidDataException(Str.T("Str.EqMainLarge"));
        var bytes = File.ReadAllBytes(mainPath); var encoding = EncodingFor(bytes); var bom = PreambleLength(bytes, encoding);
        var r = Load();
        if (r is not null && !Path.GetFullPath(engine.ConfigPath).Equals(Path.GetFullPath(r.ConfigPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Str.T("Str.EqMoved"));
        if (r is null)
        {
            if (File.Exists(ownPath) || encoding.GetString(bytes).Contains("FpsTune Equalizer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Str.T("Str.EqUnregistered"));
            r = new(engine.ConfigPath, bytes, encoding.GetBytes(IncludeBlock), null, settings);
        }
        CheckOwnFile(ownPath, r);
        var attached = FindEntry(bytes, r) >= 0;
        if (!attached && !bytes.SequenceEqual(r.OriginalMain)) throw new InvalidOperationException(Str.T("Str.EqEntryMissing"));
        var content = new UTF8Encoding(false).GetBytes(BuildConfiguration(settings));
        r = r with { Settings = settings, ContentHash = Hash(content), PreviousContentHash = File.Exists(ownPath) ? Hash(File.ReadAllBytes(ownPath)) : null };
        Save(r); // Receipt exists before the first engine write, including partial failure recovery.
        AtomicFile.WriteAllBytes(ownPath, content);
        if (!attached) AtomicFile.WriteAllBytes(mainPath, bytes[..bom].Concat(r.Prefix).Concat(bytes[bom..]).ToArray());
        if (!File.ReadAllBytes(ownPath).SequenceEqual(content) || FindEntry(File.ReadAllBytes(mainPath), r) < 0)
            throw new IOException(Str.T("Str.EqReadback"));
        Save(r with { PreviousContentHash = null });
    }
    public static bool Restore()
    {
        using var gate = SystemMutationGate.Acquire();
        var r = Load(); if (r is null) return false;
        var mainPath = Path.Combine(r.ConfigPath, "config.txt"); var ownPath = Path.Combine(r.ConfigPath, IncludeName);
        CheckOwnFile(ownPath, r);
        var bytes = File.ReadAllBytes(mainPath); var entry = FindEntry(bytes, r);
        if (entry >= 0)
        {
            var restored = bytes[..entry].Concat(bytes[(entry + r.Prefix.Length)..]).ToArray();
            AtomicFile.WriteAllBytes(mainPath, restored);
            if (!File.ReadAllBytes(mainPath).SequenceEqual(restored)) throw new IOException(Str.T("Str.EqRestoreFailed"));
        }
        else if (!bytes.SequenceEqual(r.OriginalMain)) throw new InvalidOperationException(Str.T("Str.EqRestoreChanged"));
        if (File.Exists(ownPath)) File.Delete(ownPath);
        File.Delete(ReceiptPath);
        return true;
    }
}
