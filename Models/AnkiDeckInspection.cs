namespace AnkiDeck.Models;

/// <summary>One field (Front, Back, Audio, ...) of an Anki note.</summary>
public sealed record AnkiField(string Name, string Value);

/// <summary>A single Anki note with all of its fields and tags.</summary>
public sealed record AnkiNote(
    long NoteId,
    IReadOnlyList<AnkiField> Fields,
    IReadOnlyList<string> Tags,
    bool HasHtmlMarkup)
{
    /// <summary>Returns the raw field value (including any HTML markup) or an empty string.</summary>
    public string GetField(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
        ?? string.Empty;
}

/// <summary>Result of reading an .apkg package.</summary>
public sealed class AnkiDeckInspection
{
    public required string FileName { get; init; }

    /// <summary>Name of the collection file inside the package (collection.anki2/anki21/anki21b).</summary>
    public required string CollectionEntry { get; init; }

    public required int NoteCount { get; init; }

    public required int CardCount { get; init; }

    /// <summary>Number of notes whose Front or Back field contains HTML markup (line breaks aside).</summary>
    public required int NotesWithHtmlCount { get; init; }

    public required IReadOnlyList<AnkiNote> Notes { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required TimeSpan Elapsed { get; init; }
}
