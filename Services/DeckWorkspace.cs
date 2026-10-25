using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const string MediaEntryName = "media";

    private static readonly Regex MediaReferencePattern = new(
        @"\[(?:sound|img):[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<char> InvalidFileNameChars = new("<>:\"/\\|?*");

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _workingDirectory;
    private readonly string _packagePath;
    private readonly string _databasePath;
    private readonly List<AnkiNote> _notes;
    private readonly Dictionary<string, string> _mediaEntries;
    private readonly List<MediaManifestEntry>? _mediaProtobufEntries;
    private readonly bool _mediaCompressed;
    private readonly List<PendingMedia> _pendingMedia = new();
    private readonly HashSet<string> _removedMediaNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<GeneratedAudioFile>> _generatedAudio = new();

    private DeckWorkspace(
        string workingDirectory,
        string packagePath,
        string databasePath,
        string fileName,
        ParsedDeck parsed,
        MediaManifest mediaManifest)
    {
        _workingDirectory = workingDirectory;
        _packagePath = packagePath;
        _databasePath = databasePath;
        _mediaEntries = mediaManifest.Entries;
        _mediaProtobufEntries = mediaManifest.ProtobufEntries;
        _mediaCompressed = mediaManifest.Compressed;
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

    /// <summary>When true, the notes table shows only notes whose Audio field has no sound file.</summary>
    public bool ShowNoAudioOnly { get; set; }

    /// <summary>Substring filter for the Front field (at most 20 characters).</summary>
    public string FrontFilter { get; set; } = string.Empty;

    /// <summary>Substring filter for the Back field (at most 20 characters).</summary>
    public string BackFilter { get; set; } = string.Empty;

    /// <summary>Note whose generated-audio preview row is currently expanded, if any.</summary>
    public long? AudioPreviewNoteId { get; set; }

    /// <summary>Background "refresh all audio" operation, if any.</summary>
    public BulkAudioJob? AudioJob { get; set; }

    public IReadOnlyList<GeneratedAudioFile> GetGeneratedAudioFiles(long noteId) =>
        _generatedAudio.GetValueOrDefault(noteId) ?? [];

    /// <summary>Reads a media file of the deck (existing or generated) so the browser can play it.</summary>
    public async Task<(byte[] Content, string ContentType)?> TryReadMediaAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var media in _pendingMedia)
        {
            if (string.Equals(media.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return (media.Content, GetContentType(fileName));
            }
        }

        if (_removedMediaNames.Contains(fileName))
        {
            return null;
        }

        string? entryName = null;
        foreach (var pair in _mediaEntries)
        {
            if (string.Equals(pair.Value, fileName, StringComparison.OrdinalIgnoreCase))
            {
                entryName = pair.Key;
                break;
            }
        }

        if (entryName is null)
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(_packagePath);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        var content = buffer.ToArray();
        if (IsZstd(content))
        {
            content = DecompressZstd(content);
        }

        return (content, GetContentType(fileName));
    }

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };

    private static bool IsAudioFile(string fileName) =>
        GetContentType(fileName).StartsWith("audio/", StringComparison.Ordinal);

    /// <summary>Returns the size in bytes of every media file known to the workspace.</summary>
    public IReadOnlyDictionary<string, long> GetMediaSizes()
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var media in _pendingMedia)
        {
            sizes[media.FileName] = media.Content.Length;
        }

        var missing = new List<(string EntryId, string FileName)>();
        foreach (var pair in _mediaEntries)
        {
            if (_removedMediaNames.Contains(pair.Value)
                || sizes.ContainsKey(pair.Value)
                || string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            if (_mediaProtobufEntries is not null
                && int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < _mediaProtobufEntries.Count)
            {
                sizes[pair.Value] = _mediaProtobufEntries[index].Size;
            }
            else
            {
                missing.Add((pair.Key, pair.Value));
            }
        }

        if (missing.Count > 0)
        {
            using var archive = ZipFile.OpenRead(_packagePath);
            foreach (var (entryId, fileName) in missing)
            {
                var entry = archive.GetEntry(entryId);
                if (entry is not null)
                {
                    sizes[fileName] = entry.Length;
                }
            }
        }

        return sizes;
    }

    /// <summary>
    /// Counts audio files registered in the deck (including generated but unexported ones) and
    /// how many of them are not referenced by any [sound:...] in any note field.
    /// </summary>
    public (int Total, int Unused) GetAudioStatistics()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in _notes)
        {
            foreach (var field in note.Fields)
            {
                foreach (var fileName in AudioReference.AllFileNames(field.Value))
                {
                    referenced.Add(fileName);
                }
            }
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in _mediaEntries.Values)
        {
            if (!_removedMediaNames.Contains(fileName) && IsAudioFile(fileName))
            {
                files.Add(fileName);
            }
        }

        foreach (var media in _pendingMedia)
        {
            if (IsAudioFile(media.FileName))
            {
                files.Add(media.FileName);
            }
        }

        var unused = files.Count(fileName => !referenced.Contains(fileName));
        return (files.Count, unused);
    }

    public void SetGeneratedAudio(long noteId, IReadOnlyList<GeneratedAudioFile> files)
    {
        _generatedAudio[noteId] = files.ToList();
        AudioPreviewNoteId = noteId;
        ExpandedNoteId = null;
    }

    /// <summary>
    /// Writes the generated audio files into the note's Audio field and registers them for export.
    /// File names are derived from the text segment each file was generated from.
    /// </summary>
    public async Task<IReadOnlyList<string>> AttachAudioAsync(
        AnkiNote note,
        IReadOnlyList<GeneratedAudioFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Нет сгенерированных аудиофайлов.");
        }

        var audioField = note.Fields.FirstOrDefault(field => IsAudioField(field.Name));
        if (audioField is null)
        {
            throw new InvalidOperationException("У заметки нет поля Audio.");
        }

        var oldNames = AudioReference.AllFileNames(audioField.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // References in all other fields/notes keep the old file alive.
        var remainingReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var otherNote in _notes)
        {
            foreach (var field in otherNote.Fields)
            {
                if (otherNote.NoteId == note.NoteId && IsAudioField(field.Name))
                {
                    continue;
                }

                foreach (var referenced in AudioReference.AllFileNames(field.Value))
                {
                    remainingReferences.Add(referenced);
                }
            }
        }

        foreach (var oldName in oldNames)
        {
            if (!remainingReferences.Contains(oldName))
            {
                RemoveMedia(oldName);
            }
        }

        var fileNames = new List<string>(files.Count);
        foreach (var file in files)
        {
            var fileName = CreateMediaFileName(file.Text, file.Audio.Extension);
            fileNames.Add(fileName);

            _pendingMedia.RemoveAll(media =>
                string.Equals(media.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            _pendingMedia.Add(new PendingMedia(fileName, file.Audio.Content));
            _removedMediaNames.Remove(fileName);
        }

        var audioValue = string.Concat(fileNames.Select(fileName => $"[sound:{fileName}]"));
        var fields = note.Fields
            .Select(field => IsAudioField(field.Name)
                ? new AnkiField(field.Name, audioValue)
                : field)
            .ToList();

        await using var connection = await OpenWriteConnectionAsync(cancellationToken);
        await WriteNoteAsync(connection, transaction: null, note.NoteId, fields, cancellationToken);

        ReplaceNote(new AnkiNote(note.NoteId, fields, HtmlMarkup.IsPresent(fields)));
        return fileNames;
    }

    private void RemoveMedia(string fileName)
    {
        _pendingMedia.RemoveAll(media =>
            string.Equals(media.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        _removedMediaNames.Add(fileName);
    }

    /// <summary>Confirms the audio files generated for the note's preview row.</summary>
    public async Task<IReadOnlyList<string>> ConfirmGeneratedAudioAsync(
        AnkiNote note,
        CancellationToken cancellationToken = default)
    {
        if (!_generatedAudio.TryGetValue(note.NoteId, out var files))
        {
            throw new InvalidOperationException("Аудио для этой заметки не найдено — сгенерируйте его заново.");
        }

        var fileNames = await AttachAudioAsync(note, files, cancellationToken);

        _generatedAudio.Remove(note.NoteId);
        AudioPreviewNoteId = null;

        return fileNames;
    }

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
            var mediaManifest = ReadMediaManifest(packagePath);

            return new DeckWorkspace(
                workingDirectory.FullName, packagePath, databasePath, fileName, parsed, mediaManifest);
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

    /// <summary>Builds an .apkg package containing the current collection database and generated media.</summary>
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

        using var source = ZipFile.OpenRead(_packagePath);

        var mediaChanged = _pendingMedia.Count > 0 || _removedMediaNames.Count > 0;

        var pendingByName = new Dictionary<string, PendingMedia>(StringComparer.OrdinalIgnoreCase);
        var pendingOrder = new List<string>();
        foreach (var media in _pendingMedia)
        {
            if (!pendingByName.ContainsKey(media.FileName))
            {
                pendingOrder.Add(media.FileName);
            }

            pendingByName[media.FileName] = media;
        }

        var originalEntries = _mediaEntries
            .Select(pair => (Id: pair.Key, Name: pair.Value))
            .OrderBy(item => int.TryParse(item.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue)
            .ToList();

        var finalMedia = new List<(string Name, string? SourceId, PendingMedia? Pending)>();
        if (mediaChanged)
        {
            foreach (var item in originalEntries)
            {
                if (_removedMediaNames.Contains(item.Name) || pendingByName.ContainsKey(item.Name))
                {
                    continue;
                }

                finalMedia.Add((item.Name, item.Id, null));
            }

            foreach (var name in pendingOrder)
            {
                finalMedia.Add((name, null, pendingByName[name]));
            }
        }

        var mediaMap = mediaChanged
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(_mediaEntries, StringComparer.Ordinal);

        var manifest = new List<MediaManifestEntry>();
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source.Entries)
        {
            if (!mediaChanged || !_mediaEntries.ContainsKey(entry.FullName))
            {
                usedEntryNames.Add(entry.FullName);
            }
        }

        MediaManifestEntry? FindOriginalManifest(string id)
        {
            if (_mediaProtobufEntries is null
                || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= _mediaProtobufEntries.Count)
            {
                return null;
            }

            return _mediaProtobufEntries[index];
        }

        using (var targetStream = new FileStream(
            exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        using (var target = new ZipArchive(targetStream, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                if (string.Equals(entry.FullName, CollectionEntry, StringComparison.Ordinal))
                {
                    var databaseEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    await using var databaseStream = databaseEntry.Open();
                    await databaseStream.WriteAsync(database, cancellationToken);
                    continue;
                }

                if (mediaChanged
                    && (string.Equals(entry.FullName, MediaEntryName, StringComparison.Ordinal)
                        || _mediaEntries.ContainsKey(entry.FullName)))
                {
                    continue;
                }

                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                await using var destination = created.Open();
                await using var input = entry.Open();
                await input.CopyToAsync(destination, cancellationToken);
            }

            if (mediaChanged)
            {
                var nextMediaId = 0;
                foreach (var item in finalMedia)
                {
                    byte[] stored;
                    MediaManifestEntry manifestEntry;

                    if (item.Pending is { } pending)
                    {
                        var content = pending.Content;
                        stored = _mediaCompressed ? CompressZstd(content) : content;
                        manifestEntry = new MediaManifestEntry(
                            item.Name,
                            (uint)content.Length,
                            SHA1.HashData(content));
                    }
                    else
                    {
                        var sourceEntry = source.GetEntry(item.SourceId!);
                        if (sourceEntry is null)
                        {
                            continue;
                        }

                        using var buffer = new MemoryStream();
                        await using (var input = sourceEntry.Open())
                        {
                            await input.CopyToAsync(buffer, cancellationToken);
                        }

                        stored = buffer.ToArray();

                        var original = FindOriginalManifest(item.SourceId!);
                        if (original is { } metadata)
                        {
                            manifestEntry = new MediaManifestEntry(item.Name, metadata.Size, metadata.Sha1);
                        }
                        else
                        {
                            var raw = _mediaCompressed && IsZstd(stored) ? DecompressZstd(stored) : stored;
                            manifestEntry = new MediaManifestEntry(
                                item.Name,
                                (uint)raw.Length,
                                SHA1.HashData(raw));
                        }
                    }

                    string entryName;
                    do
                    {
                        entryName = nextMediaId.ToString(CultureInfo.InvariantCulture);
                        nextMediaId++;
                    }
                    while (usedEntryNames.Contains(entryName));

                    usedEntryNames.Add(entryName);
                    mediaMap[entryName] = item.Name;
                    manifest.Add(manifestEntry);

                    var created = target.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var destination = created.Open();
                    await destination.WriteAsync(stored, cancellationToken);
                }

                var mediaEntry = target.CreateEntry(MediaEntryName, CompressionLevel.Optimal);
                await using var mediaStream = mediaEntry.Open();

                if (_mediaCompressed && _mediaProtobufEntries is not null)
                {
                    var protobuf = SerializeMediaProtobuf(manifest);
                    var compressed = CompressZstd(protobuf);
                    await mediaStream.WriteAsync(compressed, cancellationToken);
                }
                else
                {
                    await JsonSerializer.SerializeAsync(mediaStream, mediaMap, cancellationToken: cancellationToken);
                }
            }
        }

        var bytes = await File.ReadAllBytesAsync(exportPath, cancellationToken);
        File.Delete(exportPath);
        return bytes;
    }

    private static string Clean(AnkiNote note, string fieldName) =>
        HtmlMarkup.Strip(note.GetField(fieldName));

    private static bool IsDisplayField(string name) => HtmlMarkup.IsMarkupField(name);

    private static bool IsAudioField(string name) =>
        string.Equals(name, "Audio", StringComparison.OrdinalIgnoreCase);

    private string CreateMediaFileName(string front, string extension)
    {
        var baseName = BuildBaseName(front);
        var candidate = $"{baseName}.{extension}";
        if (!MediaNameExists(candidate))
        {
            return candidate;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            candidate = $"{baseName}-{RandomSuffix(6)}.{extension}";
            if (!MediaNameExists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Не удалось подобрать уникальное имя аудиофайла.");
    }

    private bool MediaNameExists(string fileName) =>
        (!_removedMediaNames.Contains(fileName)
            && _mediaEntries.Values.Any(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)))
        || _pendingMedia.Any(media => string.Equals(media.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static string BuildBaseName(string front)
    {
        var text = HtmlMarkup.Strip(front).Replace("<br>", " ", StringComparison.OrdinalIgnoreCase);
        text = MediaReferencePattern.Replace(text, " ");

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsControl(character) && !InvalidFileNameChars.Contains(character))
            {
                builder.Append(character);
            }
        }

        var name = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        if (name.Length > 80)
        {
            name = name[..80].TrimEnd();
        }

        if (name.Length == 0)
        {
            name = "audio";
        }

        if (ReservedFileNames.Contains(name))
        {
            name += "_";
        }

        return name;
    }

    private static string RandomSuffix(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, alphabet, static (span, chars) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    private static MediaManifest ReadMediaManifest(string packagePath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entry = archive.GetEntry(MediaEntryName);
            if (entry is null)
            {
                return new MediaManifest(map, null, false);
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var raw = buffer.ToArray();

            if (IsZstd(raw))
            {
                // New .apkg format: a zstd-compressed protobuf manifest; the media files
                // themselves are stored zstd-compressed under numeric zip entry names.
                var protobufEntries = ParseMediaProtobuf(DecompressZstd(raw));
                for (var index = 0; index < protobufEntries.Count; index++)
                {
                    map[index.ToString(CultureInfo.InvariantCulture)] = protobufEntries[index].Name;
                }

                return new MediaManifest(map, protobufEntries, true);
            }

            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MediaManifest(map, null, false);
            }

            foreach (var item in document.RootElement.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String)
                {
                    map[item.Name] = item.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ZstdException)
        {
            // A malformed media table must not prevent reading the deck's notes.
        }

        return new MediaManifest(map, null, false);
    }

    private static List<MediaManifestEntry> ParseMediaProtobuf(byte[] data)
    {
        var entries = new List<MediaManifestEntry>();
        var index = 0;

        while (index < data.Length)
        {
            var key = ReadVarint(data, ref index);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref index));
                if (length < 0 || index + length > data.Length)
                {
                    break;
                }

                var chunk = data.AsSpan(index, length);
                index += length;

                if (field == 1)
                {
                    entries.Add(ParseMediaProtobufEntry(chunk));
                }
            }
            else if (wire == 0)
            {
                ReadVarint(data, ref index);
            }
            else if (wire == 5)
            {
                index += 4;
            }
            else if (wire == 1)
            {
                index += 8;
            }
            else
            {
                break;
            }
        }

        return entries;
    }

    private static MediaManifestEntry ParseMediaProtobufEntry(ReadOnlySpan<byte> data)
    {
        var name = string.Empty;
        var size = 0u;
        var sha1 = Array.Empty<byte>();
        var index = 0;

        while (index < data.Length)
        {
            var key = ReadVarint(data, ref index);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref index));
                if (length < 0 || index + length > data.Length)
                {
                    break;
                }

                var chunk = data.Slice(index, length);
                index += length;

                if (field == 1)
                {
                    name = Encoding.UTF8.GetString(chunk);
                }
                else if (field == 3)
                {
                    sha1 = chunk.ToArray();
                }
            }
            else if (wire == 0)
            {
                var value = ReadVarint(data, ref index);
                if (field == 2)
                {
                    size = (uint)value;
                }
            }
            else if (wire == 5)
            {
                index += 4;
            }
            else if (wire == 1)
            {
                index += 8;
            }
            else
            {
                break;
            }
        }

        return new MediaManifestEntry(name, size, sha1);
    }

    private static byte[] SerializeMediaProtobuf(List<MediaManifestEntry> entries)
    {
        using var output = new MemoryStream();

        foreach (var entry in entries)
        {
            using var nested = new MemoryStream();

            WriteTag(nested, 1, 2);
            WriteBytes(nested, Encoding.UTF8.GetBytes(entry.Name));
            WriteTag(nested, 2, 0);
            WriteVarint(nested, entry.Size);
            WriteTag(nested, 3, 2);
            WriteBytes(nested, entry.Sha1);

            WriteTag(output, 1, 2);
            WriteVarint(output, (ulong)nested.Length);
            nested.WriteTo(output);
        }

        return output.ToArray();
    }

    private static ulong ReadVarint(byte[] data, ref int index)
    {
        ulong result = 0;
        var shift = 0;

        while (index < data.Length)
        {
            var value = data[index++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int index)
    {
        ulong result = 0;
        var shift = 0;

        while (index < data.Length)
        {
            var value = data[index++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    private static void WriteTag(Stream stream, int field, int wire) =>
        WriteVarint(stream, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static bool IsZstd(byte[] data) =>
        data.Length >= 4
        && data[0] == 0x28
        && data[1] == 0xB5
        && data[2] == 0x2F
        && data[3] == 0xFD;

    private static byte[] DecompressZstd(byte[] data)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }

    private static byte[] CompressZstd(byte[] data)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(data).ToArray();
    }

    private readonly record struct MediaManifest(
        Dictionary<string, string> Entries,
        List<MediaManifestEntry>? ProtobufEntries,
        bool Compressed);

    private readonly record struct MediaManifestEntry(string Name, uint Size, byte[] Sha1);

    private readonly record struct PendingMedia(string FileName, byte[] Content);

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
