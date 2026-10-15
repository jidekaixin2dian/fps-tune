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
}
