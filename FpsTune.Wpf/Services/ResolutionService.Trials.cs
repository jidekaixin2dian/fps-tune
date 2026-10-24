using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal enum ResolutionTrialPhase { None, Starting, Waiting, Reverting, RetryNeeded, Failed, Confirmed, Reverted, Unknown }
internal sealed record ResolutionTrialState(string? Token, ResolutionTrialPhase Phase, DateTimeOffset? Deadline,
    string? Error, bool Pending, bool CanConfirm, DisplayMode? Mode);

internal static partial class ResolutionService
{
    // One status per physical display. Status never grants authority to restore: only the validated receipt does.
    private sealed record TrialStatus(string Identity, string Token, ResolutionTrialPhase Phase, string? Error = null,
        int? GuardPid = null, long? GuardStartedTicks = null, string? GuardPath = null);
    private static string TrialPath(string id) => DisplayBackupFiles.PathFor(DirectoryPath, "trial", id);
    private static TrialStatus? LoadTrial(string id)
    {
        var path = TrialPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException(Str.T("Str.ResolutionTrialInvalid"));
        var state = JsonSerializer.Deserialize<TrialStatus>(File.ReadAllText(path));
        if (state is null || !string.Equals(state.Identity, id, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(state.Token, "N", out _) || !Enum.IsDefined(state.Phase)
            || state.Error?.Length > 8192 || state.GuardPath?.Length > 4096)
            throw new InvalidDataException(Str.T("Str.ResolutionTrialInvalid"));
        return state;
    }
    private static void SaveTrial(TrialStatus state) => AtomicFile.WriteAllTextDurable(TrialPath(state.Identity),
        JsonSerializer.Serialize(state), new UTF8Encoding(false));
    private static void RecordTrial(Receipt receipt, ResolutionTrialPhase phase, string? error = null)
    {
        TrialStatus? previous = null;
        try { previous = LoadTrial(receipt.Identity); }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        var state = previous?.Token == receipt.Token ? previous : new TrialStatus(receipt.Identity, receipt.Token, phase);
        var detail = error is null ? null : PrivacyScrub.Sanitize(error);
        SaveTrial(state with { Phase = phase, Error = detail?[..Math.Min(8192, detail.Length)] });
    }
    private static void RecordTrialFailure(Receipt receipt, ResolutionTrialPhase phase, Exception error)
    {
        try { RecordTrial(receipt, phase, error.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { /* Keep the original recovery error. Missing status is treated as unprotected, never as success. */ }
    }
    private static bool GuardAlive(TrialStatus? state)
    {
        if (GuardOverride is not null) return true;
        if (state?.GuardPid is not > 0 || state.GuardStartedTicks is null || state.GuardPath is null) return false;
        try
        {
            using var process = Process.GetProcessById(state.GuardPid.Value);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.GuardStartedTicks
                && ProcessIdentity.MatchesPath(process, state.GuardPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal static ResolutionTrialState TrialState(string id)
    {
        var receipt = Load(id);
        TrialStatus? state = null; string? statusError = null;
        try { state = LoadTrial(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { statusError = ex.Message; }
        if (receipt is { Pending: true })
        {
            if (state?.Token != receipt.Token) state = null;
            var alive = GuardAlive(state);
            var phase = state?.Phase ?? ResolutionTrialPhase.Failed;
            var error = statusError ?? state?.Error;
            if (!alive || phase is ResolutionTrialPhase.Confirmed or ResolutionTrialPhase.Reverted or ResolutionTrialPhase.None)
            { phase = ResolutionTrialPhase.Failed; error ??= Str.T("Str.ResolutionGuardStopped"); }
            else if ((phase is ResolutionTrialPhase.Waiting or ResolutionTrialPhase.Starting) && DateTimeOffset.UtcNow >= receipt.Deadline)
                phase = ResolutionTrialPhase.Reverting;
            return new(receipt.Token, phase, receipt.Deadline, error, true,
                alive && phase == ResolutionTrialPhase.Waiting && DateTimeOffset.UtcNow < receipt.Deadline, receipt.Expected);
        }
        if (receipt is not null)
            return new(receipt.Token, state?.Token == receipt.Token && state.Phase == ResolutionTrialPhase.Reverted
                ? ResolutionTrialPhase.Reverted : ResolutionTrialPhase.Confirmed, null, null, false, false, receipt.Expected);
        if (state is { Phase: ResolutionTrialPhase.Reverted }) return new(state.Token, state.Phase, null, null, false, false, null);
        return new(state?.Token, state is null && statusError is null ? ResolutionTrialPhase.None : ResolutionTrialPhase.Unknown,
            null, statusError, false, false, null);
    }
}
