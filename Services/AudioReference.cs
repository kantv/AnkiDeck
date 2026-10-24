using System.Text.RegularExpressions;

namespace AnkiDeck.Services;

/// <summary>Recognises audio attachments in Anki fields.</summary>
public static class AudioReference
{
    /// <summary>Anki stores attached audio as <c>[sound:file.mp3]</c> inside the field text.</summary>
    private static readonly Regex SoundPattern = new(
        @"\[sound:([^\]\r\n]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPresent(string? fieldValue) =>
        !string.IsNullOrEmpty(fieldValue) && SoundPattern.IsMatch(fieldValue);

    /// <summary>Returns the file name of the first [sound:...] reference, if any.</summary>
    public static string? FirstFileName(string? fieldValue)
    {
        if (string.IsNullOrEmpty(fieldValue))
        {
            return null;
        }

        var match = SoundPattern.Match(fieldValue);
        return match.Success ? match.Groups[1].Value : null;
    }
}
