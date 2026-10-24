using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AnkiDeck.Models;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace AnkiDeck.Services;

/// <summary>
/// A deck uploaded by the user: the original .apkg, a writable copy of its collection database
/// and the notes read from it. Lives on the server between requests, so edits can be applied
/// and the whole package can be exported again.
/// </summary>
public sealed class DeckWorkspace : IDisposable
{
    private const char FieldSeparator = '\u001f';
    private const string ZstdCollectionEntry = "collection.anki21b";

    private static readonly Regex MediaReferencePattern = new(
        @"\[(?:sound|img):[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _workingDirectory;
    private readonly string _packagePath;
    private readonly string _databasePath;
    private readonly List<AnkiNote> _notes;

    private DeckWorkspace(
        string workingDirectory,
        string packagePath,
        string databasePath,
        string fileName,
        ParsedDeck parsed)
    {
        _workingDirectory = workingDirectory;
        _packagePath = packagePath;
        _databasePath = databasePath;
        FileName = fileName;
        CollectionEntry = parsed.CollectionEntry;
        CardCount = parsed.CardCount;
        LoadTime = parsed.Elapsed;
        Warnings = parsed.Warnings;
        _notes = parsed.Notes.ToList();
    }

    public string Id { get; set; } = string.Empty;

    /// <summary>Last time the workspace was used, for pruning abandoned uploads.</summary>
    public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;

    public string FileName { get; }

    public string CollectionEntry { get; }

    public int CardCount { get; }

    public TimeSpan LoadTime { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<AnkiNote> Notes => _notes;

    public int NoteCount => _notes.Count;

    public int NotesWithHtmlCount => _notes.Count(note => note.HasHtmlMarkup);

    /// <summary>Note whose cleaned preview row is currently expanded, if any.</summary>
    public long? ExpandedNoteId { get; set; }

    /// <summary>When true, the notes table shows only notes that still contain HTML markup.</summary>
    public bool ShowHtmlOnly { get; set; }

    public static async Task<DeckWorkspace> CreateAsync(
        Stream package,
        string fileName,
        ApkgReader reader,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ankideck-");

        try
        {
            var packagePath = Path.Combine(workingDirectory.FullName, "deck.apkg");
            await using (var file = new FileStream(
                packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await package.CopyToAsync(file, cancellationToken);
            }

            var parsed = await reader.ParseAsync(packagePath, workingDirectory.FullName, cancellationToken);
            var databasePath = Path.Combine(workingDirectory.FullName, "collection.db");

            return new DeckWorkspace(workingDirectory.FullName, packagePath, databasePath, fileName, parsed);
        }
        catch
        {
            TryDeleteDirectory(workingDirectory.FullName);
            throw;
        }
    }

    /// <summary>Cleaned values that will be shown in the comparison row and written on confirmation.</summary>
    public (string Front, string Back, string Audio) GetPreview(AnkiNote note) =>
        (Clean(note, "Front"), Clean(note, "Back"), Clean(note, "Audio"));

    /// <summary>Removes markup from the displayed fields, stores the note in the collection and updates the list.</summary>
    public async Task<AnkiNote> ConfirmPreviewAsync(AnkiNote note, CancellationToken cancellationToken = default)
    {
        var fields = CleanFields(note);

        await using var connection = await OpenWriteConnectionAsync(cancellationToken);
        await WriteNoteAsync(connection, transaction: null, note.NoteId, fields, cancellationToken);

        var updated = new AnkiNote(note.NoteId, fields, HtmlMarkup.IsPresent(fields));
        ReplaceNote(updated);

        ExpandedNoteId = null;
        return updated;
    }

    /// <summary>Removes markup from every note that still contains it, in a single database transaction.</summary>
    public async Task<int> CleanAllAsync(CancellationToken cancellationToken = default)
    {
        var pending = _notes
            .Where(note => note.HasHtmlMarkup)
            .Select(note => (Note: note, Fields: CleanFields(note)))
            .ToList();

        ExpandedNoteId = null;

        if (pending.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenWriteConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        foreach (var item in pending)
        {
            await WriteNoteAsync(connection, transaction, item.Note.NoteId, item.Fields, cancellationToken);
        }

        transaction.Commit();

        foreach (var item in pending)
        {
            ReplaceNote(new AnkiNote(item.Note.NoteId, item.Fields, HtmlMarkup.IsPresent(item.Fields)));
        }

        return pending.Count;
    }

    /// <summary>Builds an .apkg package containing the current (edited) collection database.</summary>
    public async Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        var exportPath = Path.Combine(_workingDirectory, "export.apkg");
        if (File.Exists(exportPath))
        {
            File.Delete(exportPath);
        }

        var database = await File.ReadAllBytesAsync(_databasePath, cancellationToken);
        if (CollectionEntry == ZstdCollectionEntry)
        {
            using var compressor = new Compressor(3);
            database = compressor.Wrap(database).ToArray();
        }

        using (var source = ZipFile.OpenRead(_packagePath))
        using (var targetStream = new FileStream(
            exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        using (var target = new ZipArchive(targetStream, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                await using var destination = created.Open();

                if (string.Equals(entry.FullName, CollectionEntry, StringComparison.Ordinal))
                {
                    await destination.WriteAsync(database, cancellationToken);
                    continue;
                }

                await using var input = entry.Open();
                await input.CopyToAsync(destination, cancellationToken);
            }
        }

        var bytes = await File.ReadAllBytesAsync(exportPath, cancellationToken);
        File.Delete(exportPath);
        return bytes;
    }

    private static string Clean(AnkiNote note, string fieldName) =>
        HtmlMarkup.Strip(note.GetField(fieldName));

    private static bool IsDisplayField(string name) => HtmlMarkup.IsMarkupField(name);

    private static List<AnkiField> CleanFields(AnkiNote note) =>
        note.Fields
            .Select(field => IsDisplayField(field.Name)
                ? new AnkiField(field.Name, HtmlMarkup.Strip(field.Value))
                : field)
            .ToList();

    private void ReplaceNote(AnkiNote note)
    {
        var index = _notes.FindIndex(item => item.NoteId == note.NoteId);
        if (index >= 0)
        {
            _notes[index] = note;
        }
    }

    private async Task<SqliteConnection> OpenWriteConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateCollation("unicase", static (left, right) =>
            string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        return connection;
    }

    private static async Task WriteNoteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long noteId,
        IReadOnlyList<AnkiField> fields,
        CancellationToken cancellationToken)
    {
        var rawFields = string.Join(FieldSeparator, fields.Select(field => field.Value));
        var sortField = HtmlMarkup.Strip(fields.Count > 0 ? fields[0].Value : string.Empty);
        var checksum = ComputeChecksum(sortField);
        var modified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE notes
            SET flds = $flds, sfld = $sfld, csum = $csum, mod = $mod
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$flds", rawFields);
        command.Parameters.AddWithValue("$sfld", sortField);
        command.Parameters.AddWithValue("$csum", checksum);
        command.Parameters.AddWithValue("$mod", modified);
        command.Parameters.AddWithValue("$id", noteId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException($"Заметка {noteId} не найдена в базе колоды.");
        }
    }

    /// <summary>Anki's note checksum: the first eight hex digits of SHA-1 over the stripped sort field.</summary>
    private static long ComputeChecksum(string sortField)
    {
        var plainText = MediaReferencePattern.Replace(sortField, string.Empty);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(plainText));
        return Convert.ToInt64(Convert.ToHexString(hash)[..8], 16);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => TryDeleteDirectory(_workingDirectory);
}
