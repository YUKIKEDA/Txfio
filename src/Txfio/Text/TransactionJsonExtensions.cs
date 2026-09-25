using System.Text.Json;

namespace Txfio;

/// <summary>
/// トランザクション上の JSON の読み書き
/// </summary>
public static class TransactionJsonExtensions
{
    /// <summary>
    /// 読み取ったバイトを JSON として読む
    /// </summary>
    /// <typeparam name="T">読み取る型</typeparam>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="options">省略時は <see cref="JsonSerializer"/> の既定</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>デシリアライズした値</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="JsonException">JSON として読めない</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static async Task<T?> ReadFromJsonAsync<T>(
        this ITransaction transaction,
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using Stream stream = await transaction.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 値を JSON にして書く。コミット後の姿でファイルが無ければ Add、あれば Update
    /// </summary>
    /// <typeparam name="T">書き込む型</typeparam>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="value">書き込む値</param>
    /// <param name="options">省略時は <see cref="JsonSerializer"/> の既定</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static async Task WriteAsJsonAsync<T>(
        this ITransaction transaction,
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using MemoryStream buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, value, options, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await TransactionTextExtensions.StageAsync(transaction, path, buffer, cancellationToken)
            .ConfigureAwait(false);
    }
}
