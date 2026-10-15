namespace FpsTune.Wpf.Services;

public static partial class DisplayQualityService
{
    // NVIDIA NvApiDriverSettings.h: FRL_FPS_ID / APPIDLE_DYNAMIC_FRL_FPS_ID.
    public const uint FrameRateLimitId = 0x10835002;
    public const uint BackgroundFrameRateLimitId = 0x10835016;
    private static bool IsFrameLimit(uint id) => id is FrameRateLimitId or BackgroundFrameRateLimitId;

    public sealed record FrameLimits(uint? Foreground, uint? Background);

    public static bool IsValidFrameLimit(uint value, bool background = false)
        => value == 0 || value >= 20 && value <= (background ? 200 : 1000);

    public static void ApplyFrameLimits(string gameExe, FrameLimits limits)
    {
        if (limits.Foreground is null && limits.Background is null)
            throw new ArgumentException(Str.T("Str.FrameSelectOne"));
        if (limits.Foreground is { } f && !IsValidFrameLimit(f)
            || limits.Background is { } b && !IsValidFrameLimit(b, background: true))
            throw new ArgumentOutOfRangeException(nameof(limits), Str.T("Str.FrameRange"));
        ApplyManaged(gameExe, (session, owner) =>
        {
            if (limits.Foreground is { } foreground) session.SetSettingDword(owner, FrameRateLimitId, foreground);
            if (limits.Background is { } background) session.SetSettingDword(owner, BackgroundFrameRateLimitId, background);
        });
    }

    public static bool RestoreFrameLimits(string gameExe) => RestoreManaged(gameExe, IsFrameLimit);
}
