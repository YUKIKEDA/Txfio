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

        // 版が 1 でない JSON は、操作を解釈する前に返す（未知の種別で逆シリアル化が落ちても .txnew を消さない）
        if (IsUnsupportedVersion(payload))
        {
            return JournalReadResult.OtherVersion();
        }

        JournalDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<JournalDocument>(payload, _jsonOptions);
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
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions);
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

    // 種別が名前に無い、または path が空、または Move の newPath が空の操作は、適用も巻き戻しもできない
    private static bool IsComplete(JournalOperation operation)
    {
        if (!Enum.IsDefined(operation.Kind) || string.IsNullOrEmpty(operation.Path))
        {
            return false;
        }

        return operation.Kind != PendingChangeKind.Move || !string.IsNullOrEmpty(operation.NewPath);
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
        catch (IOException)
        {
            // 消せなくても、呼び出し側が元の例外を返す
        }
        catch (UnauthorizedAccessException)
        {
            // 消せなくても、呼び出し側が元の例外を返す
        }
    }
}
