using System.Text.Json.Serialization;

namespace AnkiDeck.Models;

/// <summary>Zvukogram API credentials and voice options, stored in a browser cookie.</summary>
public sealed class ZvukogramSettings
{
    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Male voice name used for synthesis. The JSON name stays "voice" so settings saved by
    /// earlier versions of the app keep working.
    /// </summary>
    [JsonPropertyName("voice")]
    public string MaleVoice { get; set; } = string.Empty;

    /// <summary>Female voice name, reserved for feminine nouns; not used for synthesis yet.</summary>
    public string FemaleVoice { get; set; } = string.Empty;

    public string Format { get; set; } = "mp3";

    /// <summary>Sample rate in Hz; an empty value means the service default.</summary>
    public string SampleRate { get; set; } = string.Empty;

    /// <summary>Bitrate in kbps; an empty value means the service default.</summary>
    public string Bitrate { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(MaleVoice);
}
