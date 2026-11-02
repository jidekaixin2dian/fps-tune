using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Services;

internal static class ReleaseNotesText
{
    // Release bodies are untrusted text, never a browser or executable Markdown surface.
    internal static string Format(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return Str.T("Str.ReleaseNotesEmpty");
        var text = notes[..Math.Min(notes.Length, 12000)].Replace("\r", "");
        text = Regex.Replace(text, @"!?\[([^\]\n]*)\]\([^\)\n]*\)", "$1");
        text = Regex.Replace(text, @"<[^>\n]*>", "");
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s+", "");
        text = text.Replace("**", "").Replace("`", "").Trim();
        return text.Length == 0 ? Str.T("Str.ReleaseNotesEmpty") : text;
    }

    /// <summary>本版要点：从 Release 说明里挑前几条列表项，给更新浮窗做一眼可读的摘要。</summary>
    internal static string Highlight(string? notes, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes[..Math.Min(notes.Length, 12000)].Replace("\r", "").Split('\n');
        var bullets = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;
            if (line[0] is not ('-' or '*' or '•')) continue;
            var item = Format(line.TrimStart('-', '*', '•', ' '));
            if (item.Length == 0) continue;
            bullets.Add(item.Length > 64 ? item[..63] + "…" : item);
            if (bullets.Count == max) break;
        }
        return string.Join("\n", bullets.Select(b => "· " + b));
    }
}