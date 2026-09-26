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
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 未コミットの新規ジャーナルを作成する
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    internal static Task WriteNewAsync(string journalPath, Guid transactionId, CancellationToken cancellationToken)
    {
        JournalDocument document = new JournalDocument(
            CurrentVersion,
            transactionId,
            committing: false,
            Array.Empty<JournalOperation>());
        return WriteAsync(journalPath, document, FileMode.CreateNew, cancellationToken);
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
            await WriteAsync(tempPath, document, FileMode.Create, cancellationToken).ConfigureAwait(false);

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
    /// ジャーナルを読む（JSON として読めないときは <see langword="null"/>）
    /// </summary>
    /// <param name="journalPath">読み取り元</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読めた文書（JSON として読めないときと、値が JSON の null のときは <see langword="null"/>）</returns>
    /// <exception cref="IOException">読み取りに失敗した</exception>
    internal static async Task<JournalDocument?> TryReadAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            byte[] payload = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JournalDocument>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
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
