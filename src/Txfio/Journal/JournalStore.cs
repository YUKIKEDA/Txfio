using System.Text.Json;
using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// Reads and writes journal files.
/// </summary>
internal static class JournalStore
{
    /// <summary>
    /// The version of the current journal document format.
    /// </summary>
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    /// <summary>
    /// Creates a new uncommitted journal (writes a temporary file and renames it, so the journal path never holds a partial state).
    /// </summary>
    /// <param name="journalPath">The path to write to.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the journal is written.</returns>
    /// <exception cref="IOException">A file already exists at the journal path, or the write failed.</exception>
    internal static async Task WriteNewAsync(string journalPath, Guid transactionId, CancellationToken cancellationToken)
    {
        JournalDocument document = JournalPaths.ToStored(
            new JournalDocument(
                CurrentVersion,
                transactionId,
                committing: false,
                Array.Empty<JournalOperation>()),
            MetadataNames.WorkFolderFromJournal(journalPath));
        string tempPath = MetadataNames.JournalTempPath(journalPath);
        try
        {
            await WriteAsync(tempPath, document, FileMode.Create, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, journalPath, overwrite: false);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Replaces an existing journal through a temporary file.
    /// </summary>
    /// <param name="journalPath">The path to write to.</param>
    /// <param name="document">The document to write.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the journal is written.</returns>
    internal static async Task SaveAsync(string journalPath, JournalDocument document, CancellationToken cancellationToken)
    {
        string tempPath = MetadataNames.JournalTempPath(journalPath);
        try
        {
            JournalDocument stored = JournalPaths.ToStored(document, MetadataNames.WorkFolderFromJournal(journalPath));
            await WriteAsync(tempPath, stored, FileMode.Create, cancellationToken).ConfigureAwait(false);

            // A sharing violation in File.Move becomes UnauthorizedAccessException, so open and close first (a sharing violation when opening stays IOException).
            EnsureReplaceable(journalPath);
            File.Move(tempPath, journalPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Reads a journal, and checks the version, the kinds, and the required fields.
    /// </summary>
    /// <param name="journalPath">The path to read from.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The document that was read, or why it cannot be read (not readable as JSON, a JSON null value, a kind that is not a name, an empty path, an empty Move newPath, a different version, or a combined path outside the work folder, at the work folder itself, at the metadata folder, or under the metadata folder).</returns>
    /// <exception cref="IOException">The read failed.</exception>
    internal static async Task<JournalReadResult> ReadAsync(string journalPath, CancellationToken cancellationToken)
    {
        byte[] payload = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);

        // If the version on the first line is not 1, return "different version" before interpreting operations (so .txnew files are not deleted even if deserialization fails on an unknown kind).
        if (IsUnsupportedVersion(FirstRecord(payload)))
        {
            return JournalReadResult.OtherVersion();
        }

        JournalDocument? document;
        try
        {
            document = ReadLines(payload);
        }
        catch (JsonException)
        {
            return JournalReadResult.Corrupt();
        }

        if (document is null)
        {
            return JournalReadResult.Corrupt();
        }

        if (document.Version != CurrentVersion)
        {
            return JournalReadResult.OtherVersion();
        }

        foreach (JournalOperation operation in document.Operations)
        {
            if (operation is null || !IsComplete(operation))
            {
                return JournalReadResult.Corrupt();
            }
        }

        try
        {
            document = JournalPaths.ToAbsolute(document, MetadataNames.WorkFolderFromJournal(journalPath));
        }
        catch (JsonException)
        {
            return JournalReadResult.Corrupt();
        }

        if (document is null)
        {
            return JournalReadResult.Corrupt();
        }

        return JournalReadResult.Readable(document);
    }

    /// <summary>
    /// Appends one line with the operations and created directories added to the end of the table (does not rewrite the whole journal).
    /// </summary>
    /// <param name="journalPath">The path to write to.</param>
    /// <param name="operations">The operations added to the end.</param>
    /// <param name="createdDirectories">The created directories added to the end.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the line is appended.</returns>
    internal static async Task AppendAsync(
        string journalPath,
        IReadOnlyList<JournalOperation> operations,
        IReadOnlyList<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        string workFolder = MetadataNames.WorkFolderFromJournal(journalPath);
        JournalDocument stored = JournalPaths.ToStored(
            new JournalDocument(CurrentVersion, Guid.Empty, committing: false, operations, createdDirectories),
            workFolder);
        JournalAppend record = new JournalAppend(stored.Operations, stored.CreatedDirectories);
        byte[] payload = Line(JsonSerializer.SerializeToUtf8Bytes(record, _jsonOptions));
        FileStream stream = new FileStream(
            journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await StagingFile.FlushToDiskAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the temporary file for overwriting, if there is one.
    /// </summary>
    /// <param name="journalPath">The path of the matching journal.</param>
    internal static void DeleteTemp(string journalPath)
    {
        string tempPath = MetadataNames.JournalTempPath(journalPath);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Deletes the journal file, if there is one.
    /// </summary>
    /// <param name="journalPath">The journal to delete.</param>
    /// <returns>A task that completes when the journal is deleted.</returns>
    internal static Task DeleteAsync(string journalPath)
    {
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        return Task.CompletedTask;
    }

    private static async Task WriteAsync(
        string journalPath,
        JournalDocument document,
        FileMode mode,
        CancellationToken cancellationToken)
    {
        byte[] payload = Line(JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions));
        FileStream stream = new FileStream(
            journalPath,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await StagingFile.FlushToDiskAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The first line is the document and later lines are append records; a last line without a newline is dropped as a crash during an append.
    private static JournalDocument? ReadLines(byte[] payload)
    {
        List<ReadOnlyMemory<byte>> lines = new List<ReadOnlyMemory<byte>>();
        int start = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] == (byte)'\n')
            {
                lines.Add(new ReadOnlyMemory<byte>(payload, start, i - start));
                start = i + 1;
            }
        }

        bool tornTail = start < payload.Length;
        if (tornTail)
        {
            lines.Add(new ReadOnlyMemory<byte>(payload, start, payload.Length - start));
        }

        if (lines.Count == 0)
        {
            return JsonSerializer.Deserialize<JournalDocument>(payload, _jsonOptions);
        }

        JournalDocument? document = JsonSerializer.Deserialize<JournalDocument>(lines[0].Span, _jsonOptions);
        if (document is null || lines.Count == 1)
        {
            return document;
        }

        List<JournalOperation> operations = new List<JournalOperation>(document.Operations);
        List<string> createdDirectories = new List<string>(document.CreatedDirectories);
        int complete = tornTail ? lines.Count - 1 : lines.Count;
        for (int i = 1; i < complete; i++)
        {
            JournalAppend? record = JsonSerializer.Deserialize<JournalAppend>(lines[i].Span, _jsonOptions);
            if (record is null)
            {
                throw new JsonException("A journal append record is null");
            }

            operations.AddRange(record.Append);
            createdDirectories.AddRange(record.CreatedDirectories);
        }

        return new JournalDocument(
            document.Version,
            document.TransactionId,
            document.Committing,
            operations,
            createdDirectories);
    }

    private static byte[] Line(byte[] json)
    {
        byte[] line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[json.Length] = (byte)'\n';
        return line;
    }

    // An operation whose kind is not a name, whose path is empty, or whose Move newPath is empty can be neither applied nor rolled back.
    private static bool IsComplete(JournalOperation operation)
    {
        if (!Enum.IsDefined(operation.Kind) || string.IsNullOrEmpty(operation.Path))
        {
            return false;
        }

        return operation.Kind != PendingChangeKind.Move || !string.IsNullOrEmpty(operation.NewPath);
    }

    // Check the version on the first line only (with append records after it, the whole file is not one JSON value).
    private static byte[] FirstRecord(byte[] payload)
    {
        int newline = Array.IndexOf(payload, (byte)'\n');
        if (newline < 0)
        {
            return payload;
        }

        byte[] line = new byte[newline];
        Buffer.BlockCopy(payload, 0, line, 0, newline);
        return line;
    }

    // If the version field is not the number 1, return "different version" without interpreting operations.
    private static bool IsUnsupportedVersion(byte[] payload)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(payload);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool sawVersion = false;
            bool supported = false;
            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sawVersion = true;
                supported = property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int version)
                    && version == CurrentVersion;
            }

            return sawVersion && !supported;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void EnsureReplaceable(string journalPath)
    {
        using FileStream probe = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
        _ = probe.CanWrite;
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            // Even if it cannot be deleted, the caller returns the original exception.
        }
    }
}
