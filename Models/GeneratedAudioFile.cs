namespace AnkiDeck.Models;

/// <summary>One synthesized file together with the text segment it was generated from.</summary>
public sealed record GeneratedAudioFile(string Text, GeneratedAudio Audio);
