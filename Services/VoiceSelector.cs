using System.Text.RegularExpressions;
using AnkiDeck.Models;

namespace AnkiDeck.Services;

/// <summary>Chooses the male or female voice depending on the text being synthesized.</summary>
public static class VoiceSelector
{
    /// <summary>Text that starts with "la " (any case, at least one whitespace after "la").</summary>
    private static readonly Regex LaPrefix = new(
        @"^la\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool PrefersFemaleVoice(string text) =>
        !string.IsNullOrEmpty(text) && LaPrefix.IsMatch(text);

    public static string Resolve(ZvukogramSettings settings, string text) =>
        (PrefersFemaleVoice(text) ? settings.FemaleVoice : settings.MaleVoice).Trim();
}
