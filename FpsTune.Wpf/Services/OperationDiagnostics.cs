using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record DriverValue(bool Exists, uint? Value)
{
    internal static DriverValue From(bool exists, uint value) => new(exists, exists ? value : null);
}
internal sealed record DriverSettingDiagnostic(string SettingId, DriverValue Before, DriverValue Requested,
    DriverValue? After = null, DriverValue? Original = null, DriverValue? LastApplied = null);
internal sealed record OperationDiagnostic(string Id, DateTimeOffset At, string Operation, string Target,
    string Stage, string Outcome, bool IsAdministrator, string DriverVersion, string AppVersion,
    string? ErrorType, int? NativeStatus, string? Error, IReadOnlyList<DriverSettingDiagnostic> Settings);
internal sealed record OperationDiagnosticReport(string Status, IReadOnlyList<OperationDiagnostic> Operations);

/// <summary>Bounded, local operation receipts. Diagnostic failure never changes the operation's result.</summary>
internal static partial class OperationDiagnostics
{
    private static readonly object Gate = new();
    internal const int Retention = 50;
    private sealed record Store(int SchemaVersion, List<OperationDiagnostic> Operations);
    private static string FilePath(string? root) => Path.Combine(root ?? UserDataPaths.Root, "logs", "operations.json");

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[^\s""';,]+|\b(password|passwd|token|api[_-]?key|secret)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s;,]+)")]
    private static partial Regex Credentials();
    internal static string Scrub(string? text)
    {
        var value = text ?? "";
        if (value.Length > 4096) value = value[..4096];
        return PrivacyScrub.Sanitize(Credentials().Replace(value, "<redacted>"));
    }

    internal static OperationDiagnosticReport Read(string? root = null)
    {
        try
        {
            var file = FilePath(root);
            if (!File.Exists(file)) return new("no-records", []);
            if (new FileInfo(file).Length > 1024 * 1024) return new("invalid-records", []);
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(file));
            if (store is null || store.SchemaVersion != 1 || store.Operations is null || store.Operations.Count > Retention)
                return new("invalid-records", []);
            return new("available", store.Operations.Select(Sanitize).ToArray());
        }
        catch { return new("unavailable", []); }
    }

    private static OperationDiagnostic Sanitize(OperationDiagnostic row) => row with
    {
        Id = Guid.TryParseExact(row.Id, "N", out _) ? row.Id : "invalid",
        Operation = Scrub(row.Operation), Target = Scrub(row.Target), Stage = Scrub(row.Stage),
        Outcome = Scrub(row.Outcome), DriverVersion = Scrub(row.DriverVersion), AppVersion = Scrub(row.AppVersion),
        ErrorType = Scrub(row.ErrorType), Error = Scrub(row.Error),
        Settings = (row.Settings ?? []).Take(32).Where(s => s is not null && s.Before is not null && s.Requested is not null)
            .Select(s => s with { SettingId = Scrub(s.SettingId) }).ToArray()
    };

    private static void Save(OperationDiagnostic record, string? root)
    {
        try
        {
            lock (Gate)
            {
                var file = FilePath(root);
                var previous = Read(root);
                if (File.Exists(file) && previous.Status is "invalid-records" or "unavailable")
                    File.Move(file, file + ".invalid-" + Guid.NewGuid().ToString("N"));
                var records = previous.Operations.Append(Sanitize(record)).TakeLast(Retention).ToList();
                AtomicFile.WriteAllTextDurable(file, JsonSerializer.Serialize(new Store(1, records)), new UTF8Encoding(false));
            }
        }
        catch { /* Optional diagnostics must not mask the original failure or block a successful write. */ }
    }

    internal static T Run<T>(string operation, string target, string? root, Func<Trace, T> work)
    {
        using var trace = Begin(operation, target, root);
        try { var result = work(trace); trace.Complete(); return result; }
        catch (Exception ex) { trace.Fail(ex); throw; }
    }
    internal static Trace Begin(string operation, string target, string? root = null) => new(operation, target, root);

    private static string DriverVersion()
    {
        try
        {
            var key = NativeSystem.GetMainGpuDriverKeyPath();
            return key is null ? "unavailable" : RegistryHelper.ReadValue(
                Microsoft.Win32.RegistryHive.LocalMachine, key, "DriverVersion")?.ToString() ?? "unavailable";
        }
        catch { return "unavailable"; }
    }

    internal sealed class Trace(string operation, string target, string? root) : IDisposable
    {
        private readonly List<DriverSettingDiagnostic> _settings = [];
        private readonly DateTimeOffset _at = DateTimeOffset.UtcNow;
        private string _stage = "prepare", _outcome = "interrupted";
        private Exception? _error;
        private bool _disposed;
        internal void Stage(string stage) => _stage = stage;
        internal void Complete() => _outcome = "succeeded";
        internal void Fail(Exception error) { _outcome = error is OperationCanceledException ? "cancelled" : "failed"; _error = error; }
        internal void Setting(uint id, bool before, uint beforeValue, bool requested, uint requestedValue,
            bool original, uint originalValue, DriverValue? lastApplied = null)
        {
            var key = id.ToString("X8");
            _settings.RemoveAll(s => s.SettingId == key);
            _settings.Add(new(key, DriverValue.From(before, beforeValue), DriverValue.From(requested, requestedValue),
                Original: DriverValue.From(original, originalValue), LastApplied: lastApplied));
        }
        internal void Readback(uint id, bool exists, uint value)
        {
            var index = _settings.FindIndex(s => s.SettingId == id.ToString("X8"));
            if (index >= 0) _settings[index] = _settings[index] with { After = DriverValue.From(exists, value) };
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                var native = _error;
                while (native is not null && native is not NvdrsException) native = native.InnerException;
                Save(new(Guid.NewGuid().ToString("N"), _at, operation, target, _stage, _outcome,
                    AdminHelper.IsAdministrator(), DriverVersion(), UpdateService.DisplayVersion,
                    _error?.GetType().Name, (native as NvdrsException)?.Status, _error?.Message, _settings.ToArray()), root);
            }
            catch { /* Diagnostic metadata may be unavailable. The operation's result remains authoritative. */ }
        }
    }
}
