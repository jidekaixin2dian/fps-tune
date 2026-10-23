using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

public static partial class BackupService
{
    internal sealed record InterruptedReview(string Fingerprint, string Summary, int Count);
    private sealed record ReviewedFile(string Path, byte[] Original, string Updated);
    private sealed record ReviewPlan(List<ReviewedFile> Files, string? Journal, string Summary, int Count)
    {
        internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Files, Journal }))));
    }

    internal static InterruptedReview ReviewInterrupted()
    {
        using var gate = SystemMutationGate.Acquire();
        var plan = PrepareInterruptedReview();
        return new(plan.Fingerprint, plan.Summary, plan.Count);
    }

    // Explicit confirmation registers observed post-values; it never writes the system.
    // Normal RestoreAll still checks the physical target and drift immediately before each write.
    internal static int ConfirmInterruptedReview(InterruptedReview review)
    {
        using var gate = SystemMutationGate.Acquire();
        var plan = PrepareInterruptedReview();
        if (plan.Fingerprint != review.Fingerprint) throw new InvalidOperationException(Str.T("Str.RecoveryReviewChanged"));
        var history = Path.Combine(BackupDir, "history", "review-" + Guid.NewGuid().ToString("N"));
        foreach (var file in plan.Files)
            PublishImportedFile(Path.Combine(history, Path.GetFileName(file.Path)), file.Original);
        if (plan.Journal is not null)
        {
            AtomicFile.WriteAllTextDurable(Path.Combine(history, RestoreJournalFileName), plan.Journal, new UTF8Encoding(false));
            // Clear the old fingerprint only after retaining its exact bytes and explicit review.
            // A crash here leaves the old receipts, whose ordinary ownership checks remain in force.
            ClearRestoreJournal();
        }
        foreach (var file in plan.Files)
            AtomicFile.WriteAllTextDurable(file.Path, file.Updated, new UTF8Encoding(false));
        return plan.Count;
    }

    private static ReviewPlan PrepareInterruptedReview()
    {
        if (!TryLoadRestoreJournal(out var marker, out var error)) throw new InvalidDataException(error);
        var journal = marker is null ? null : File.ReadAllText(RestoreJournalPath());
        var files = new List<ReviewedFile>(); var text = new StringBuilder(); var count = 0; var matchedMarker = marker is null;
        foreach (var file in ListBackups())
        {
            var records = ReadRecords(file, requireLocalOrigin: true);
            var original = File.ReadAllBytes(file);
            var touched = false;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                var marked = marker is not null && Path.GetFileName(file) == marker.BackupFile && index == marker.RecordIndex;
                if (marked)
                {
                    if (record.Id != marker!.RecordId || RecordFingerprint(record) != marker.RecordFingerprint)
                        throw new InvalidDataException(Str.T("Str.BackupTargetChanged"));
                    matchedMarker = true;
                }
                if (!marked && (record.Restored || record.MutationState != "pending")) continue;
                ValidateRecord(record);
                if (record.Id == GpuIdentityService.BackupId) GpuIdentityService.VerifyIdentity(record);
                var before = DescribeReviewValue(record, true);
                if (!record.Restored)
                {
                    CapturePostState(record);
                    if (record.Kind == "power-plan" && record.CreatedPlanGuid is not null)
                        record.CreatedPlanFingerprint = NativePowerSettings.Fingerprint(record.CreatedPlanGuid);
                    record.MutationState = "uncertain";
                    VerifyRestorableState(record);
                }
                text.AppendLine(Path.GetFileName(file) + " / " + (ItemCatalog.All.FirstOrDefault(i => i.Id == record.Id)?.Name ?? record.Id));
                if (record.Kind == "registry") text.AppendLine(record.Hive + "\\" + record.Path);
                text.AppendLine(Str.T("Str.RecoveryReviewOriginal") + before);
                text.AppendLine(record.Restored ? Str.T("Str.RecoveryReviewAlreadyRestored") : Str.T("Str.RecoveryReviewCurrent") + DescribeReviewValue(record, false));
                text.AppendLine();
                count++; touched = true;
            }
            if (touched) files.Add(new(file, original, SerializeRecords(file, records)));
        }
        if (!matchedMarker) throw new InvalidDataException(Str.T("Str.BackupTargetChanged"));
        return new(files, journal, text.ToString(), count);
    }

    private static string DescribeReviewValue(BackupRecord r, bool original) => r.Kind switch
    {
        "registry" => r.Name + " = " + ReviewValue(original ? r.Existed : r.PostExisted, original ? r.OldValue : r.PostValue)
            + (r.SecondaryName is null ? "" : "; " + r.SecondaryName + " = " + ReviewValue(original ? r.SecondaryExisted : r.SecondaryPostExisted,
                original ? r.SecondaryValue : r.SecondaryPostValue)),
        "service" => r.ServiceName + " / " + Str.T("Str.RecoveryReviewService", original ? r.OldStartValue : r.PostStartValue,
            original ? r.OldState : r.PostState, original ? r.OldDelayedValue : r.PostDelayedValue),
        "power-plan" => Str.T("Str.RecoveryReviewPlan", original ? r.OldActiveGuid : r.PostActiveGuid, r.CreatedPlanGuid),
        "power-setting" => r.TargetPlanGuid + " / " + (original ? r.OldPowerValue : r.PostPowerValue),
        "power-tuning" => r.TargetPlanGuid + " / USB=" + (original ? r.OldUsbValue : r.PostUsbValue) + " / Boost=" + (original ? r.OldBoostValue : r.PostBoostValue),
        "power-aspm" => r.TargetPlanGuid + " / " + (original ? r.OldAspmValue : r.PostAspmValue),
        _ => (original ? r.OldState : r.PostState) ?? "?"
    };

    private static string ReviewValue(bool? exists, object? value)
        => exists == false ? Str.T("Str.RecoveryReviewAbsent") : JsonSerializer.Serialize(value);
}
