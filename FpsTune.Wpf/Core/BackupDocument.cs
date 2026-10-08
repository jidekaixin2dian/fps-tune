using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

internal sealed class BackupDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BackupRecord> Records { get; set; } = new();
}

internal sealed class BackupCompatibilityException : IOException
{
    internal BackupCompatibilityException(string message) : base(message) { }
}

public static partial class BackupService
{
    private const string V2BackupPrefix = "v2-backup-";
    private static readonly JsonSerializerOptions BackupJsonOptions = new() { WriteIndented = true, MaxDepth = 64 };

    internal static List<BackupRecord> DecodeRecords(string json)
    {
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var records = parsed.RootElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<BackupRecord>>(json, BackupJsonOptions)
            : DecodeDocument(json).Records;
        if (records is null || records.Count > 10000) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return records;
    }

    private static BackupDocument DecodeDocument(string json)
    {
        var document = JsonSerializer.Deserialize<BackupDocument>(json, BackupJsonOptions);
        if (document is not null && document.SchemaVersion != 2)
            throw new BackupCompatibilityException(Str.T("Str.BackupFormatInvalid"));
        if (document is null || document.Records is null ||
            !Guid.TryParse(document.OperationId, out _) || !Guid.TryParse(document.OriginId, out _))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return document;
    }

    internal static List<BackupRecord> ReadRecords(string file, bool requireLocalOrigin = false)
    {
        if (new FileInfo(file).Length > 10 * 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        var json = File.ReadAllText(file, Encoding.UTF8);
        if (requireLocalOrigin && Path.GetFileName(file).StartsWith(V2BackupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var document = DecodeDocument(json);
            if (document.OriginId != GetOriginId()) throw new BackupCompatibilityException(Str.T("Str.BackupOtherOrigin"));
        }
        return DecodeRecords(json);
    }

    private static string GetOriginId()
    {
        Directory.CreateDirectory(BackupDir);
        var path = Path.Combine(BackupDir, ".origin-id");
        if (!File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")); stream.Write(bytes); stream.Flush(true);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        var id = File.ReadAllText(path).Trim();
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return id;
    }

    private static string SerializeRecords(string file, IReadOnlyList<BackupRecord> records)
    {
        if (!Path.GetFileName(file).StartsWith(V2BackupPrefix, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(records, BackupJsonOptions);
        var document = File.Exists(file) ? DecodeDocument(File.ReadAllText(file)) : new BackupDocument { OriginId = GetOriginId() };
        document.Records = records.ToList();
        return JsonSerializer.Serialize(document, BackupJsonOptions);
    }

    internal static IReadOnlyList<BackupRecord> AppendCapture(string file, string id, string? gamePath)
    {
        var records = ReadRecords(file, requireLocalOrigin: true);
        var captured = CreateBackupRecords(id, gamePath);
        foreach (var record in captured) record.MutationState = "pending";
        records.AddRange(captured);
        AtomicFile.WriteAllTextDurable(file, SerializeRecords(file, records), new UTF8Encoding(false));
        return captured;
    }

    internal static string OperationId(string file) => DecodeDocument(File.ReadAllText(file)).OperationId;
    internal static bool IsOperationResolved(string file)
    {
        if (!IsSafeBackupFilePath(file)) return false;
        var current = File.Exists(file) ? file : file + ".restored";
        return File.Exists(current) && ReadRecords(current, requireLocalOrigin: true).All(r => r.Restored);
    }
    internal static void EnsureNoPendingWrites(IReadOnlyList<string> ids)
    {
        foreach (var file in ListBackups())
        {
            foreach (var record in ReadRecords(file))
                if (!record.Restored && record.MutationState == "pending" && ids.Contains(record.Id))
                    throw new InvalidOperationException(Str.T("Str.BackupPendingWrite"));
        }
    }

    internal static void CompleteCapture(string file, OptimizationApplyResult result)
    {
        var records = ReadRecords(file, requireLocalOrigin: true);
        foreach (var record in records.Where(r => r.Id == result.Id))
        {
            if (result.Ok && !result.Changed) { record.Restored = true; record.MutationState = "unchanged"; continue; }
            record.MutationState = result.Ok ? "applied" : "uncertain";
            if (record.Kind == "registry")
            {
                var snapshot = RegistryHelper.ReadSnapshot(Enum.Parse<Microsoft.Win32.RegistryHive>(record.Hive), record.Path, record.Name);
                record.PostExisted = snapshot.Existed; record.PostValue = snapshot.Value;
                if (record.SecondaryExisted.HasValue)
                {
                    var second = RegistryHelper.ReadSnapshot(Enum.Parse<Microsoft.Win32.RegistryHive>(record.SecondaryHive!), record.SecondaryPath!, record.SecondaryName!);
                    record.SecondaryPostExisted = second.Existed; record.SecondaryPostValue = second.Value;
                }
            }
            else if (record.Kind == "power-plan")
            {
                record.PostActiveGuid = NativePowerSettings.RequireActiveGuid();
                if (result.Ok && result.Changed && record.CreatedPlanGuid is not null)
                    record.CreatedPlanFingerprint = NativePowerSettings.Fingerprint(record.CreatedPlanGuid);
            }
            else if (record.Kind == "power-setting") record.PostPowerValue = PowerOption.For(record.Id).Read(record.TargetPlanGuid!);
            else if (record.Kind == "power-tuning")
            {
                record.PostUsbValue = NativePowerSettings.ReadAc(record.TargetPlanGuid!, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
                record.PostBoostValue = NativePowerSettings.ReadAc(record.TargetPlanGuid!, "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7");
            }
            else if (record.Kind == "power-aspm") record.PostAspmValue = NativePowerSettings.ReadAc(record.TargetPlanGuid!, "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5");
            else if (record.Kind == "service")
            {
                record.PostStartValue = NativeSystem.GetServiceStartValue(record.ServiceName!);
                record.PostState = NativeSystem.GetServiceState(record.ServiceName!);
                var delayed = RegistryHelper.ReadSnapshot(Microsoft.Win32.RegistryHive.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Services\" + record.ServiceName, "DelayedAutostart");
                if (delayed.Existed && (delayed.Kind != Microsoft.Win32.RegistryValueKind.DWord || delayed.Value is not int))
                    throw new InvalidDataException(Str.T("Str.BackupTargetChanged"));
                record.PostDelayedExisted = delayed.Existed;
                record.PostDelayedValue = delayed.Existed ? (int)delayed.Value! : null;
            }
            else if (record.Kind == "bcdedit") record.PostState = NativeSystem.GetDynamicTickState().ToString().ToLowerInvariant();
            else if (record.Kind == "hibernate") record.PostState = NativeSystem.IsHibernateEnabled() ? "on" : "off";
        }
        AtomicFile.WriteAllTextDurable(file, SerializeRecords(file, records), new UTF8Encoding(false));
    }

    private static bool SameValue(object? a, object? b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static void VerifyRestorableState(BackupRecord record)
    {
        if (record.Id == GpuIdentityService.BackupId) GpuIdentityService.VerifyIdentity(record);
        if (record.MutationState == "legacy") return;
        if (record.MutationState is not ("applied" or "uncertain" or "unchanged"))
            throw new BackupCompatibilityException(Str.T("Str.BackupPendingWrite"));
        if (record.Kind == "registry")
        {
            if (!record.PostExisted.HasValue) throw new BackupCompatibilityException(Str.T("Str.BackupPendingWrite"));
            var current = RegistryHelper.ReadSnapshot(Enum.Parse<Microsoft.Win32.RegistryHive>(record.Hive), record.Path, record.Name);
            if (!(current.Existed == record.PostExisted && SameValue(current.Value, record.PostValue)) &&
                !(current.Existed == record.Existed && SameValue(current.Value, record.OldValue)))
                throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
            if (record.SecondaryExisted.HasValue)
            {
                var second = RegistryHelper.ReadSnapshot(Enum.Parse<Microsoft.Win32.RegistryHive>(record.SecondaryHive!), record.SecondaryPath!, record.SecondaryName!);
                if (!(second.Existed == record.SecondaryPostExisted && SameValue(second.Value, record.SecondaryPostValue)) &&
                    !(second.Existed == record.SecondaryExisted && SameValue(second.Value, record.SecondaryValue)))
                    throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
            }
        }
        else if (record.Kind == "service")
        {
            if (record.PostState is not ("RUNNING" or "STOPPED" or "STOP_PENDING") || !record.PostStartValue.HasValue || !record.PostDelayedExisted.HasValue)
                throw new BackupCompatibilityException(Str.T("Str.BackupPendingWrite"));
            var start = NativeSystem.GetServiceStartValue(record.ServiceName!);
            var state = NativeSystem.GetServiceState(record.ServiceName!);
            var delayed = RegistryHelper.ReadSnapshot(Microsoft.Win32.RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\" + record.ServiceName, "DelayedAutostart");
            if (start != record.PostStartValue && start != record.OldStartValue ||
                state != record.PostState && state != record.OldState && !(record.PostState == "STOP_PENDING" && state == "STOPPED") ||
                !(delayed.Existed == record.PostDelayedExisted && SameValue(delayed.Value, record.PostDelayedValue) ||
                  delayed.Existed == record.OldDelayedExisted && SameValue(delayed.Value, record.OldDelayedValue)))
                throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
        }
        else if (record.Kind is "bcdedit" or "hibernate")
        {
            if (record.PostState is null) throw new BackupCompatibilityException(Str.T("Str.BackupPendingWrite"));
            var current = record.Kind == "bcdedit" ? NativeSystem.GetDynamicTickState().ToString().ToLowerInvariant()
                : NativeSystem.IsHibernateEnabled() ? "on" : "off";
            if (current != record.PostState && current != record.OldState)
                throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
        }
        else if (record.Kind == "power-plan")
        {
            var current = NativePowerSettings.RequireActiveGuid();
            if (current != record.PostActiveGuid && current != record.OldActiveGuid)
                throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
        }
        else if (record.Kind == "power-setting")
        {
            RequirePowerTarget(record);
            var current = PowerOption.For(record.Id).Read(record.TargetPlanGuid!);
            if (current != record.PostPowerValue && current != record.OldPowerValue)
                throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
        }
        else if (record.Kind is "power-tuning" or "power-aspm")
        {
            RequirePowerTarget(record);
            void Check(string group, string setting, int? post, int? original)
            {
                var current = NativePowerSettings.ReadAc(record.TargetPlanGuid!, group, setting);
                if (current != post && current != original) throw new BackupCompatibilityException(Str.T("Str.BackupTargetChanged"));
            }
            if (record.Kind == "power-aspm") Check("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", record.PostAspmValue, record.OldAspmValue);
            else
            {
                Check("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", record.PostUsbValue, record.OldUsbValue);
                Check("54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", record.PostBoostValue, record.OldBoostValue);
            }
        }
    }
}
