using System.Text.Json;
using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// ジャーナルファイルの読み書き
/// </summary>
internal static class JournalStore
{
    /// <summary>
    /// 現在のジャーナル文書形式の版
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
    /// 未コミットの新規ジャーナルを作成する（一時ファイルに書いてから rename し、ジャーナルのパスに途中の状態を残さない）
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    /// <exception cref="IOException">ジャーナルのパスに既にファイルがある、または書き込みに失敗した</exception>
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
    /// 既存ジャーナルを、一時ファイル経由で置き換える
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="document">書き出す文書</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    internal static async Task SaveAsync(string journalPath, JournalDocument document, CancellationToken cancellationToken)
    {
        string tempPath = MetadataNames.JournalTempPath(journalPath);
        try
        {
            JournalDocument stored = JournalPaths.ToStored(document, MetadataNames.WorkFolderFromJournal(journalPath));
            await WriteAsync(tempPath, stored, FileMode.Create, cancellationToken).ConfigureAwait(false);

            // File.Move の共有違反は UnauthorizedAccessException になるため、先に開いて閉じる（開けないときの共有違反は IOException のまま返す）
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
    /// ジャーナルを読み、版と種別と必須の欄を確かめる
    /// </summary>
    /// <param name="journalPath">読み取り元</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読めた文書、または読めない理由（JSON として読めない、値が JSON の null である、種別が名前に無い、path が空、または Move の newPath が空、版が違う、結合したパスがワークフォルダの外、ワークフォルダ自身、メタデータフォルダ、またはメタデータフォルダの配下である）</returns>
    /// <exception cref="IOException">読み取りに失敗した</exception>
    internal static async Task<JournalReadResult> ReadAsync(string journalPath, CancellationToken cancellationToken)
    {
        byte[] payload = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);

        // 1 行目の版が 1 でなければ、操作を解釈する前に版が違うと返す（未知の種別で逆シリアル化が落ちても .txnew を消さない）
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
    /// 表の末尾に足した操作と作成ディレクトリを、ジャーナルへ 1 行追記する（全体は書き直さない）
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="operations">末尾に足した操作</param>
    /// <param name="createdDirectories">末尾に足した作成ディレクトリ</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>追記の完了</returns>
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
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 上書き用の一時ファイルがあれば消す
    /// </summary>
    /// <param name="journalPath">対応するジャーナルのパス</param>
    internal static void DeleteTemp(string journalPath)
    {
        string tempPath = MetadataNames.JournalTempPath(journalPath);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// ジャーナルファイルがあれば削除する
    /// </summary>
    /// <param name="journalPath">削除対象</param>
    /// <returns>削除の完了</returns>
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
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // 1 行目は文書、2 行目以降は追記レコードであり、改行で終わっていない最後の行は、追記の途中で落ちたものとして捨てる
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
                throw new JsonException("ジャーナルの追記レコードが null です");
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

    // 種別が名前に無い、または path が空、または Move の newPath が空の操作は、適用も巻き戻しもできない
    private static bool IsComplete(JournalOperation operation)
    {
        if (!Enum.IsDefined(operation.Kind) || string.IsNullOrEmpty(operation.Path))
        {
            return false;
        }

        return operation.Kind != PendingChangeKind.Move || !string.IsNullOrEmpty(operation.NewPath);
    }

    // 版の確認は 1 行目だけにする（追記レコードが続くと、ファイル全体は 1 個の JSON ではない）
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

    // 版の欄が数値の 1 でなければ、操作を解釈せず版が違うと返す
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
            // 消せなくても、呼び出し側が元の例外を返す
        }
    }
}
