using System.Text.Json.Serialization;

namespace AnkiDeck.Models;

/// <summary>Zvukogram API credentials and voice options, stored in a browser cookie.</summary>
public sealed class ZvukogramSettings
{
    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string Voice { get; set; } = string.Empty;

    public string Format { get; set; } = "mp3";

    /// <summary>Sample rate in Hz; an empty value means the service default.</summary>
    public string SampleRate { get; set; } = string.Empty;

    /// <summary>Bitrate in kbps; an empty value means the service default.</summary>
    public string Bitrate { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(Voice);
}
