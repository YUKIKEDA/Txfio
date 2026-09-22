using System.Text.Json;

namespace Txfio;

/// <summary>
/// ジャーナルファイルの読み書き
/// </summary>
internal static class JournalStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>
    /// 未コミットの新規ジャーナルを作成する
    /// </summary>
    /// <param name="journalPath">書き込み先</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    internal static async Task WriteNewAsync(string journalPath, Guid transactionId, CancellationToken cancellationToken)
    {
        JournalDocument document = new JournalDocument(CurrentVersion, transactionId, committing: false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

        FileStream stream = new FileStream(
            journalPath,
            FileMode.CreateNew,
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
            return JsonSerializer.Deserialize<JournalDocument>(payload, JsonOptions);
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
}
