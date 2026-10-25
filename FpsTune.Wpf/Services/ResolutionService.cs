using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record ResolutionPlan(DisplayMode Mode, bool CreateCustom, uint? Scaling, string Detail);
internal sealed record ResolutionSnapshot(IccDisplayInfo Display, DisplayMode Current, IReadOnlyList<DisplayMode> Modes,
    uint? Scaling, string ScalingDetail, bool Restorable, bool Pending);

/// <summary>Durable per-monitor transaction. A separate process rolls back even if the UI stops pumping.</summary>
internal static partial class ResolutionService
{
    internal static IDisplayModeApi? ApiOverride;
    internal static INvidiaDisplayModes? NvidiaOverride;
    internal static Action<string>? GuardOverride;
    private static IDisplayModeApi Api => ApiOverride ?? new WindowsDisplayModeApi();
    private static INvidiaDisplayModes Nvidia => NvidiaOverride ?? new NvidiaDisplayModes();
    internal static string DirectoryPath => Path.Combine(UserDataPaths.Root, "display-resolution");
    private static string PathFor(string id) => DisplayBackupFiles.PathFor(DirectoryPath, "resolution", id);
    private sealed record Receipt(string Identity, string Name, string Connection, DisplayMode Original, DisplayMode Expected,
        uint? OriginalScaling, uint? ExpectedScaling, DisplayMode Previous, uint? PreviousScaling,
        bool CustomTrial, bool Pending, string Token, DateTimeOffset Deadline);

    private static Receipt Parse(string json)
    {
        var r = JsonSerializer.Deserialize<Receipt>(json) ?? throw new InvalidDataException(Str.T("Str.Resolution.ResolutionService.1"));
        if (string.IsNullOrWhiteSpace(r.Identity) || r.Identity.Length > 4096 || r.Connection is null || r.Connection.Length > 65536
            || r.Name is null || !Guid.TryParseExact(r.Token, "N", out _) || r.Original is null || r.Expected is null || r.Previous is null
            || new[] { r.OriginalScaling, r.ExpectedScaling, r.PreviousScaling }.Any(s => s is not null && s is not (1 or 2 or 3 or 5 or 6 or 7 or 8)))
            throw new InvalidDataException(Str.T("Str.Resolution.ResolutionService.2"));
        r.Original.Validate(); r.Expected.Validate(); r.Previous.Validate(); return r;
    }
    private static Receipt? Load(string id)
    {
        var path = PathFor(id); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException(Str.T("Str.Resolution.ResolutionService.3"));
        var r = Parse(File.ReadAllText(path));
        if (!r.Identity.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Str.T("Str.Resolution.ResolutionService.4"));
        return r;
    }
    private static void Save(Receipt r) => AtomicFile.WriteAllTextDurable(PathFor(r.Identity), JsonSerializer.Serialize(r), new UTF8Encoding(false));
    private static void Complete(Receipt r)
    {
        // Preserve the last receipt before retiring it, including unsuccessful trial evidence.
        DisplayBackupFiles.Archive(PathFor(r.Identity)); File.Delete(PathFor(r.Identity));
    }
    internal static void ValidateTransferredBackup(string name, byte[] bytes)
    {
        var r = Parse(Encoding.UTF8.GetString(bytes));
        if (name != Path.GetFileName(PathFor(r.Identity)) || r.Pending)
            throw new InvalidDataException(Str.T("Str.Resolution.ResolutionService.5"));
    }
    private static string Connection() => JsonSerializer.Serialize(DisplayTopology.GetDisplays().OrderBy(DisplayTopology.Identity, StringComparer.OrdinalIgnoreCase)
        .Select(d => new { Id = DisplayTopology.Identity(d), d.GdiDeviceName, d.AdapterIdLow, d.AdapterIdHigh, d.SourceId, d.AssociationSubKey, d.Independent }));
    private static IccDisplayInfo Require(Receipt r)
    {
        var display = DisplayTopology.Require(r.Identity);
        if (Connection() != r.Connection) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.6"));
        return display;
    }
    internal static ResolutionSnapshot Read(string id)
    {
        var display = DisplayTopology.Require(id); var current = Api.Read(display); var modes = Api.Modes(display);
        uint? scaling = null; string detail;
        try
        {
            scaling = Nvidia.ReadScaling(display);
            detail = Str.T("Str.Resolution.ResolutionService.7") + ScalingName(scaling.Value);
            if (scaling is not (1 or 2 or 3 or 5 or 6 or 7 or 8)) { detail += Str.T("Str.Resolution.ResolutionService.8"); scaling = null; }
        }
        catch (Exception ex) { detail = Str.T("Str.Resolution.ResolutionService.9") + ex.Message; }
        var r = Load(id); DisplayTopology.ValidatePhysicalConnection(display);
        return new(display, current, modes, scaling, detail, r is not null, r?.Pending == true);
    }
    internal static string ScalingName(uint scaling) => scaling switch
    {
        1 => Str.T("Str.Resolution.ResolutionService.10"), 2 => Str.T("Str.Resolution.ResolutionService.11"), 3 => Str.T("Str.Resolution.ResolutionService.12"), 5 => Str.T("Str.Resolution.ResolutionService.13"),
        6 => Str.T("Str.Resolution.ResolutionService.14"), 7 => Str.T("Str.Resolution.ResolutionService.15"), 8 => Str.T("Str.Resolution.ResolutionService.16"), _ => Str.T("Str.Resolution.ResolutionService.17", scaling)
    };
    internal static ResolutionPlan Plan(string id, int width, int height, int hz, uint? scaling)
    {
        var display = DisplayTopology.Require(id); var current = Api.Read(display);
        var requested = current with { Width = width, Height = height, Hz = hz };
        requested.Validate();
        if (scaling is not null)
        {
            var original = Nvidia.ReadScaling(display);
            if (original is not (1 or 2 or 3 or 5 or 6 or 7 or 8)) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.18"));
            Nvidia.SetScaling(display, scaling.Value, testOnly: true);
        }
        var match = Api.Modes(display).FirstOrDefault(m => m.Width == width && m.Height == height && m.Hz == hz);
        if (match is not null)
        {
            Api.Test(display, match); DisplayTopology.ValidatePhysicalConnection(display);
            return new(match, false, scaling, Str.T("Str.Resolution.ResolutionService.19"));
        }
        Nvidia.ProbeCustom(display, requested); DisplayTopology.ValidatePhysicalConnection(display);
        return new(requested, true, scaling, Str.T("Str.Resolution.ResolutionService.20"));
    }
    internal static string Begin(string id, ResolutionPlan plan)
    {
        using var gate = SystemMutationGate.Acquire();
        // Never trust the draft snapshot after a hotplug or an external display change.
        var fresh = Plan(id, plan.Mode.Width, plan.Mode.Height, plan.Mode.Hz, plan.Scaling);
        if (fresh != plan) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.21"));
        var display = DisplayTopology.Require(id); var before = Api.Read(display); var old = Load(id);
        foreach (var target in BackupTargets().Where(t => t.Identity is not null && t.Identity != id))
            if (Load(target.Identity!)?.Pending == true) throw new InvalidOperationException(Str.T("Str.ResolutionOtherTrialPending"));
        if (old?.Pending == true) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.22"));
        if (old is not null)
        {
            _ = Require(old);
            if (before != old.Expected && before != old.Original) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.23"));
            CheckScaling(display, old, before == old.Original ? old.OriginalScaling : old.ExpectedScaling);
        }
        uint? beforeScaling = old?.ExpectedScaling;
        if (plan.Scaling is not null || old?.OriginalScaling is not null) beforeScaling = Nvidia.ReadScaling(display);
        else if (plan.CreateCustom)
        {
            var scale = Nvidia.ReadScaling(display);
            if (scale is 1 or 2 or 3 or 5 or 6 or 7 or 8) beforeScaling = scale;
        }
        var r = new Receipt(id, string.IsNullOrEmpty(display.FriendlyName) ? display.GdiDeviceName : display.FriendlyName, Connection(), old?.Original ?? before, plan.Mode,
            old?.OriginalScaling ?? beforeScaling, plan.Scaling ?? beforeScaling, before, beforeScaling,
            false, true, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(25));
        if (old is not null) DisplayBackupFiles.Archive(PathFor(id));
        Save(r);
        try
        {
            SaveTrial(new(id, r.Token, ResolutionTrialPhase.Starting));
            StartGuard(r); _ = Require(r); DisplayTopology.ValidatePhysicalConnection(display);
            if (plan.CreateCustom)
            {
                // Durable before the native call: an error or process exit can still leave a hardware trial.
                r = r with { CustomTrial = true }; Save(r);
                Nvidia.TryCustom(display, plan.Mode);
            }
            else Api.Apply(display, plan.Mode);
            WaitForMode(display, r.Expected, null);
            if (plan.Scaling is { } scaling) Nvidia.SetScaling(display, scaling);
            WaitForMode(display, r.Expected, r.ExpectedScaling);
            return r.Token;
        }
        catch (Exception original)
        {
            try { RollbackCore(r, false); }
            catch (Exception rollback)
            {
                RecordTrialFailure(r, ResolutionTrialPhase.RetryNeeded, rollback);
                throw new InvalidOperationException(original.Message + Str.T("Str.Resolution.ResolutionService.24") + rollback.Message + Str.T("Str.Resolution.ResolutionService.25"), original);
            }
            throw new InvalidOperationException(original.Message + Str.T("Str.Resolution.ResolutionService.26"), original);
        }
    }
    private static void CheckScaling(IccDisplayInfo display, Receipt r, uint? expected)
    {
        if (r.OriginalScaling is not null && expected is not null && Nvidia.ReadScaling(display) != expected)
            throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.27"));
    }
    private static void Verify(IccDisplayInfo display, DisplayMode expected, uint? scaling)
    {
        DisplayTopology.ValidatePhysicalConnection(display);
        if (Api.Read(display) != expected) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.28"));
        if (scaling is not null && Nvidia.ReadScaling(display) != scaling) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.29"));
    }
    private static void WaitForMode(IccDisplayInfo display, DisplayMode expected, uint? scaling)
    {
        // NVAPI may return before Windows finishes rebuilding its display state.
        // A mode switch can legitimately change SDR/WCG; retain every physical identity check.
        // Color tools continue using ValidateConnection, including its color-mode guard.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { Verify(display, expected, scaling); return; }
            catch (InvalidOperationException) when (elapsed.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(100); }
        }
    }
    internal static void Confirm(string id, string token)
    {
        using var gate = SystemMutationGate.Acquire();
        var r = Load(id) ?? throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.30"));
        if (!r.Pending || r.Token != token || !TrialState(id).CanConfirm) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.31"));
        var display = Require(r); Verify(display, r.Expected, r.ExpectedScaling);
        if (r.CustomTrial) Nvidia.SaveCustom(display); // Save only while the verified trial is still on hardware.
        Save(r with { Pending = false, CustomTrial = false });
        RecordTrial(r, ResolutionTrialPhase.Confirmed);
    }
    internal static bool Restore(string? id)
        => RestoreTrial(id, null);
    internal static bool RestoreTrial(string? id, string? token)
    {
        if (id is null) return false;
        using var gate = SystemMutationGate.Acquire(); var r = Load(id); if (r is null) return false;
        if (token is not null && (r.Token != token || !r.Pending)) return false;
        try { RollbackCore(r, !r.Pending); return true; }
        catch (Exception ex) { RecordTrialFailure(r, ResolutionTrialPhase.RetryNeeded, ex); throw; }
    }
    private static void FinishRollback(Receipt r, DisplayMode goal, uint? scaling)
    {
        RecordTrial(r, ResolutionTrialPhase.Reverted);
        if (goal == r.Original && scaling == r.OriginalScaling) Complete(r);
        else Save(r with { Expected = goal, ExpectedScaling = scaling, Pending = false, CustomTrial = false });
    }
    private static void RollbackCore(Receipt r, bool original)
    {
        var display = Require(r); var current = Api.Read(display);
        var goal = original ? r.Original : r.Previous; var scaling = original ? r.OriginalScaling : r.PreviousScaling;
        if (current != r.Expected && current != r.Previous && current != r.Original)
            throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.32"));
        if (r.OriginalScaling is not null)
        {
            var now = Nvidia.ReadScaling(display);
            if (now != r.ExpectedScaling && now != r.PreviousScaling && now != r.OriginalScaling)
                throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.33"));
        }
        if (r.CustomTrial)
        {
            // A driver can restore the Windows mode itself while retaining an unsaved custom trial.
            // Retire that trial even when the OS already reports the previous/original mode.
            Nvidia.RevertTrial(display);
            r = r with { CustomTrial = false }; Save(r);
            WaitForMode(display, r.Previous, null);
            current = Api.Read(display);
        }
        if (current == goal && (scaling is null || Nvidia.ReadScaling(display) == scaling))
        {
            FinishRollback(r, goal, scaling);
            return;
        }
        DisplayTopology.ValidatePhysicalConnection(display); Api.Test(display, goal); Api.Apply(display, goal);
        WaitForMode(display, goal, null);
        if (scaling is not null) Nvidia.SetScaling(display, scaling.Value);
        WaitForMode(display, goal, scaling);
        FinishRollback(r, goal, scaling);
    }
    internal static IReadOnlyList<DisplayBackupTarget> BackupTargets()
    {
        var targets = new List<DisplayBackupTarget>();
        foreach (var file in DisplayBackupFiles.List(DirectoryPath, "resolution"))
        {
            try { var r = Parse(File.ReadAllText(file)); _ = Load(r.Identity); targets.Add(new(r.Identity, r.Name)); }
            catch (Exception ex) { targets.Add(new(null, Path.GetFileName(file), ex.Message)); }
        }
        return targets;
    }
    private static void StartGuard(Receipt receipt)
    {
        var token = receipt.Token;
        if (GuardOverride is not null)
        { GuardOverride(token); SaveTrial(new(receipt.Identity, token, ResolutionTrialPhase.Waiting)); return; }
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FpsTune.Resolution." + token);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.34")))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-ResolutionGuard"); start.ArgumentList.Add(token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.35"));
        if (!ready.WaitOne(TimeSpan.FromSeconds(5))) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionService.36"));
        SaveTrial(new(receipt.Identity, token, ResolutionTrialPhase.Waiting, GuardPid: process.Id,
            GuardStartedTicks: process.StartTime.ToUniversalTime().Ticks, GuardPath: start.FileName));
    }
    internal static int RunGuard(string token, int attempts = 70)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return 2;
        Receipt? r = null;
        foreach (var file in DisplayBackupFiles.List(DirectoryPath, "resolution"))
        {
            try
            {
                if (new FileInfo(file).Length > 128 * 1024) continue;
                var candidate = Parse(File.ReadAllText(file));
                if (candidate.Pending && candidate.Token == token && PathFor(candidate.Identity) == file)
                { r = candidate; break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (r is null) return 0;
        using var ready = EventWaitHandle.OpenExisting("Local\\FpsTune.Resolution." + token); ready.Set();
        Exception? failure = null;
        for (var i = 0; i < Math.Clamp(attempts, 1, 70); i++)
        {
            Thread.Sleep(1000);
            var current = Load(r.Identity);
            if (current is null || !current.Pending || current.Token != token) return 0;
            if (DateTimeOffset.UtcNow < current.Deadline) continue;
            try { RestoreTrial(r.Identity, token); return 0; }
            catch (Exception ex) { failure = ex; }
        }
        // Mark only this trial. A delayed old guard must not alter the next trial's state.
        using (SystemMutationGate.Acquire())
            if (Load(r.Identity) is { Pending: true } current && current.Token == token)
                RecordTrialFailure(current, ResolutionTrialPhase.Failed, failure ?? new InvalidOperationException(Str.T("Str.ResolutionGuardStopped")));
        return 1;
    }
    internal static DateTimeOffset? Deadline(string id) => Load(id) is { Pending: true } r ? r.Deadline : null;
}
