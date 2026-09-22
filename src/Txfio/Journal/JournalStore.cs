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
    /// 既存ジャーナルを上書きする
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="document">書き出す文書</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    internal static Task SaveAsync(string journalPath, JournalDocument document, CancellationToken cancellationToken)
    {
        return WriteAsync(journalPath, document, FileMode.Create, cancellationToken);
    }

    /// <summary>
    /// ジャーナルを読む（壊れているか読めないときは <see langword="null"/>）
    /// </summary>
    /// <param name="journalPath">読み取り元</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読めた文書（失敗時は <see langword="null"/>）</returns>
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
        catch (IOException)
        {
            return null;
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
}
