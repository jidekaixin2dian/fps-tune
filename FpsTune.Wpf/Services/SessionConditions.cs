using System.IO;
using System.Security.Cryptography;
using System.Text;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

// Optional additive metadata: schema-1 sessions remain readable with Conditions == null.
public sealed record SessionConditions(string? GameKey = null, string? GameName = null,
    int? Width = null, int? Height = null, string? Quality = null, string? Scene = null,
    int? TargetSeconds = null);

public static class SessionConditionRules
{
    public static SessionConditions CaptureGame(string? path, SessionConditions? input)
    {
        var conditions = input ?? new SessionConditions();
        var normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path).ToUpperInvariant();
        return conditions with {
            GameKey = normalized is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
            GameName = normalized is null ? null : GamePathService.LabelFor(path!),
            Quality = Clean(conditions.Quality), Scene = Clean(conditions.Scene)
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsValid(SessionConditions? c) => c is null ||
        (c.Width is null && c.Height is null || c.Width is >= 320 and <= 16384 && c.Height is >= 200 and <= 16384)
        && (c.TargetSeconds is null || c.TargetSeconds is >= 1 and <= 14400)
        && ValidText(c.Quality, 120) && ValidText(c.Scene, 160) && ValidText(c.GameName, 160)
        && (c.GameKey is null || c.GameKey.Length == 64 && c.GameKey.All(Uri.IsHexDigit));

    private static bool ValidText(string? value, int limit)
        => value is null || value.Length <= limit && !value.Any(char.IsControl);

    public static string Resolution(SessionConditions? c)
        => c?.Width is { } w && c.Height is { } h ? $"{w} × {h}" : Str.T("Str.ConditionsUnknown");

    public static string Describe(SessionConditions? c) => string.Join(" · ",
        c?.GameName ?? Str.T("Str.ConditionsUnknown"), Resolution(c),
        c?.Quality ?? Str.T("Str.ConditionsUnknown"), c?.Scene ?? Str.T("Str.ConditionsUnknown"));

    public static IReadOnlyList<string> Differences(PerformanceSession a, PerformanceSession b)
    {
        var issues = new List<string>();
        var x = a.Conditions; var y = b.Conditions;
        Check("Str.ConditionGame", x?.GameKey, y?.GameKey);
        Check("Str.ConditionResolution", x?.Width is not null && x.Height is not null ? $"{x.Width}x{x.Height}" : null,
            y?.Width is not null && y.Height is not null ? $"{y.Width}x{y.Height}" : null);
        Check("Str.ConditionQuality", x?.Quality, y?.Quality);
        Check("Str.ConditionScene", x?.Scene, y?.Scene);
        Check("Str.ConditionTarget", x?.TargetSeconds?.ToString(), y?.TargetSeconds?.ToString());
        if (a.IntervalSeconds != b.IntervalSeconds) issues.Add(Str.T("Str.ConditionIntervalDiff"));
        if (a.EndedAt is not { } ae || b.EndedAt is not { } be)
            issues.Add(Str.T("Str.ConditionDurationUnknown"));
        else
        {
            var ad = (ae - a.StartedAt).TotalSeconds; var bd = (be - b.StartedAt).TotalSeconds;
            if (Math.Abs(ad - bd) > Math.Max(2, Math.Max(ad, bd) * 0.05)) issues.Add(Str.T("Str.ConditionDurationDiff"));
            if (x?.TargetSeconds is { } at && Math.Abs(ad - at) > Math.Max(2, at * 0.05)
                || y?.TargetSeconds is { } bt && Math.Abs(bd - bt) > Math.Max(2, bt * 0.05))
                issues.Add(Str.T("Str.ConditionTargetMissed"));
        }
        if (a.Fps is null || b.Fps is null) issues.Add(Str.T("Str.ConditionFpsMissing"));
        return issues;

        void Check(string label, string? av, string? bv)
        {
            if (string.IsNullOrWhiteSpace(av) || string.IsNullOrWhiteSpace(bv))
                issues.Add(Str.T("Str.ConditionMissing", Str.T(label)));
            else if (!string.Equals(av.Trim(), bv.Trim(), StringComparison.OrdinalIgnoreCase))
                issues.Add(Str.T("Str.ConditionDifferent", Str.T(label)));
        }
    }

    public static SessionConditions? Scrub(SessionConditions? c) => c is null ? null : c with {
        GameName = c.GameName is null ? null : PrivacyScrub.Sanitize(c.GameName),
        Quality = c.Quality is null ? null : PrivacyScrub.Sanitize(c.Quality),
        Scene = c.Scene is null ? null : PrivacyScrub.Sanitize(c.Scene)
    };
}
