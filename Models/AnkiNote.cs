namespace AnkiDeck.Models;

/// <summary>One field (Front, Back, Audio, ...) of an Anki note.</summary>
public sealed record AnkiField(string Name, string Value);

/// <summary>A single Anki note with all of its fields.</summary>
public sealed record AnkiNote(
    long NoteId,
    IReadOnlyList<AnkiField> Fields,
    bool HasHtmlMarkup)
{
    /// <summary>Returns the raw field value (including any HTML markup) or an empty string.</summary>
    public string GetField(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
        ?? string.Empty;
}
