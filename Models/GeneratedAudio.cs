namespace AnkiDeck.Models;

/// <summary>Audio returned by the speech service, kept only until the user confirms it.</summary>
public sealed record GeneratedAudio(byte[] Content, string Format, double Duration)
{
    public string ContentType => Format switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "opus" => "audio/ogg",
        "flac" => "audio/flac",
        _ => "application/octet-stream",
    };

    /// <summary>File extension derived from the response format (fallback: mp3).</summary>
    public string Extension
    {
        get
        {
            var cleaned = new string(Format.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return cleaned.Length is > 0 and <= 5 ? cleaned : "mp3";
        }
    }
}
