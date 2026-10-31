namespace FpsTune.Wpf.Services;

public static partial class DisplayQualityService
{
    public sealed record CompetitiveSettings(bool SmoothVrr, bool VrrAndReflexConfirmed, bool MaximumPower, uint? Foreground, uint? Background, bool ChangeSync = true);
    private static bool IsCompetitive(uint id) => id is VSyncModeId or PowerModeId or FrameRateLimitId or BackgroundFrameRateLimitId;
    public static bool HasCompetitiveBackup(string exe) => ReadBackup(exe)?.Settings.Any(s => IsCompetitive(s.SettingId)) == true;
    public static void ApplyCompetitiveSettings(string exe, CompetitiveSettings settings)
    {
        if (settings.ChangeSync && settings.SmoothVrr && !settings.VrrAndReflexConfirmed)
            throw new ArgumentException(Str.T("Str.TuningConfirmVrr"));
        if (settings.Foreground is { } f && !IsValidFrameLimit(f) || settings.Background is { } b && !IsValidFrameLimit(b, true))
            throw new ArgumentOutOfRangeException(nameof(settings), Str.T("Str.FrameRange"));
        ApplyManaged(exe, (session, profile) =>
        {
            if (settings.ChangeSync) WriteVSyncMode(session, profile, settings.SmoothVrr ? VSyncMode.ForceOn : VSyncMode.ForceOff);
            if (settings.MaximumPower) session.SetSettingDword(profile, PowerModeId, (uint)PowerMode.PreferMax);
            if (settings.Foreground is { } foreground) session.SetSettingDword(profile, FrameRateLimitId, foreground);
            if (settings.Background is { } background) session.SetSettingDword(profile, BackgroundFrameRateLimitId, background);
        });
    }
    public static bool RestoreCompetitiveSettings(string exe) => RestoreManaged(exe, IsCompetitive);
}
