using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AnkiDeck.Models;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace AnkiDeck.Services;

/// <summary>Notes and metadata read from an .apkg package.</summary>
public sealed record ParsedDeck(
    string CollectionEntry,
    int CardCount,
    IReadOnlyList<AnkiNote> Notes,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

/// <summary>
/// Reads an Anki deck package (.apkg) and extracts the notes it contains.
/// </summary>
/// <remarks>
/// An .apkg file is a zip archive with a copy of the Anki collection database:
/// <list type="bullet">
/// <item><c>collection.anki21b</c> — current format, zstd-compressed SQLite database;</item>
/// <item><c>collection.anki21</c> / <c>collection.anki2</c> — older formats, plain SQLite databases.</item>
/// </list>
/// The database contains a <c>notes</c> table (the actual text, fields separated by 0x1F),
/// a <c>cards</c> table (one row per card generated from a note) and the note type definitions
/// that provide the field names.
/// </remarks>
public sealed class ApkgReader
{
    private const char FieldSeparator = '\u001f';
    private const string ZstdCollectionEntry = "collection.anki21b";

    private static readonly string[] CollectionEntries =
    [
        ZstdCollectionEntry,
        "collection.anki21",
        "collection.anki2",
    ];

    /// <summary>Extracts the collection database into <paramref name="workDirectory"/> and reads its notes.</summary>
    public async Task<ParsedDeck> ParseAsync(
        string packagePath,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var (databasePath, collectionEntry) = await ExtractCollectionAsync(packagePath, workDirectory, cancellationToken);

        return await ReadCollectionAsync(databasePath, collectionEntry, started, cancellationToken);
    }

    /// <summary>Extracts the newest collection database found in the archive (decompressing it when needed).</summary>
    private static async Task<(string DatabasePath, string EntryName)> ExtractCollectionAsync(
        string packagePath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Файл не похож на архив .apkg: не удалось прочитать zip-контейнер.");
        }

        using (archive)
        {
            foreach (var candidate in CollectionEntries)
            {
                var entry = archive.GetEntry(candidate);
                if (entry is null)
                {
                    continue;
                }

                var databasePath = Path.Combine(workDirectory, "collection.db");
                await using (var source = entry.Open())
                await using (var target = new FileStream(
                    databasePath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    if (candidate == ZstdCollectionEntry)
                    {
                        using var compressed = new MemoryStream();
                        await source.CopyToAsync(compressed, cancellationToken);

                        try
                        {
                            using var decompressor = new Decompressor();
                            var compressedBytes = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
                            var decompressed = decompressor.Unwrap(compressedBytes);

                            await target.WriteAsync(decompressed.ToArray(), cancellationToken);
                        }
                        catch (ZstdException exception)
                        {
                            throw new InvalidDataException(
                                "Не удалось распаковать сжатый файл коллекции (collection.anki21b). Возможно, архив повреждён.",
                                exception);
                        }
                    }
                    else
                    {
                        await source.CopyToAsync(target, cancellationToken);
                    }
                }

                return (databasePath, candidate);
            }
        }

        throw new InvalidDataException(
            "В архиве .apkg не найден файл коллекции (collection.anki2, collection.anki21 или collection.anki21b).");
    }

    private static async Task<ParsedDeck> ReadCollectionAsync(
        string databasePath,
        string collectionEntry,
        Stopwatch started,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("Не удалось открыть базу данных колоды внутри .apkg.", exception);
        }

        // Schema 18 declares text columns with a custom "unicase" collation that Anki registers
        // when it opens the database. Without it every query touching those columns fails with
        // "no query solution", so provide an equivalent case-insensitive comparison.
        connection.CreateCollation("unicase", static (left, right) =>
            string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        var fieldNames = await ReadFieldNamesAsync(connection, cancellationToken);
        var cardCount = await TableExistsAsync(connection, "cards", cancellationToken)
            ? (int)await CountAsync(connection, "cards", cancellationToken)
            : 0;

        var notes = new List<AnkiNote>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, mid, flds FROM notes ORDER BY id";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var noteId = reader.GetInt64(0);
                var noteTypeId = reader.GetInt64(1);
                var rawFields = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

                var fields = SplitFields(rawFields, fieldNames.GetValueOrDefault(noteTypeId));

                notes.Add(new AnkiNote(noteId, fields, HtmlMarkup.IsPresent(fields)));
            }
        }

        var warnings = new List<string>();
        if (notes.Count == 0)
        {
            warnings.Add("В файле не найдено ни одной заметки.");
        }

        return new ParsedDeck(collectionEntry, cardCount, notes, warnings, started.Elapsed);
    }

    private static List<AnkiField> SplitFields(string rawFields, IReadOnlyList<string>? names)
    {
        var parts = rawFields.Split(FieldSeparator);
        var fields = new List<AnkiField>(parts.Length);

        for (var index = 0; index < parts.Length; index++)
        {
            var name = names is not null && index < names.Count && !string.IsNullOrWhiteSpace(names[index])
                ? names[index]
                : $"Поле {index + 1}";

            fields.Add(new AnkiField(name, parts[index]));
        }

        return fields;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : 0;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count && count > 0;
    }

    /// <summary>Reads field names per note type: from the "fields" table (schema 18) or from the legacy "col.models" JSON.</summary>
    private static async Task<Dictionary<long, List<string>>> ReadFieldNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "fields", cancellationToken))
        {
            return await ReadFieldNamesFromTableAsync(connection, cancellationToken);
        }

        if (await TableExistsAsync(connection, "col", cancellationToken))
        {
            return await ReadFieldNamesFromLegacyJsonAsync(connection, cancellationToken);
        }

        return new Dictionary<long, List<string>>();
    }

    private static async Task<Dictionary<long, List<string>>> ReadFieldNamesFromTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<long, List<string>>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ntid, name FROM fields ORDER BY ntid, ord";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var noteTypeId = reader.GetInt64(0);
            if (!fields.TryGetValue(noteTypeId, out var names))
            {
                names = new List<string>();
                fields[noteTypeId] = names;
            }

            names.Add(reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
        }

        return fields;
    }

    private static async Task<Dictionary<long, List<string>>> ReadFieldNamesFromLegacyJsonAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<long, List<string>>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT models FROM col LIMIT 1";
        var rawModels = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (rawModels is null)
        {
            return fields;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawModels);
        }
        catch (JsonException)
        {
            return fields;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return fields;
            }

            foreach (var model in document.RootElement.EnumerateObject())
            {
                if (!long.TryParse(model.Name, out var modelId)
                    || model.Value.ValueKind != JsonValueKind.Object
                    || !model.Value.TryGetProperty("flds", out var modelFields)
                    || modelFields.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var names = new List<string>();
                foreach (var field in modelFields.EnumerateArray())
                {
                    names.Add(field.ValueKind == JsonValueKind.Object
                              && field.TryGetProperty("name", out var fieldName)
                              && fieldName.ValueKind == JsonValueKind.String
                        ? fieldName.GetString() ?? string.Empty
                        : string.Empty);
                }

                fields[modelId] = names;
            }
        }

        return fields;
    }
}
