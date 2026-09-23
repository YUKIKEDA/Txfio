using System.Text;

namespace Txfio;

/// <summary>
/// トランザクション上の文字列の読み書き
/// </summary>
public static class TransactionTextExtensions
{
    private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 読み取ったバイトを文字列にする。エンコーディングは BOM を見て決める
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読み取った文字列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task<string> ReadAllTextAsync(
        this ITransaction transaction,
        string path,
        CancellationToken cancellationToken = default)
    {
        return ReadAllTextAsync(transaction, path, Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// 読み取ったバイトを、指定したエンコーディングで文字列にする。BOM があればそちらを優先する
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="encoding">BOM が無いときのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読み取った文字列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> または <paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static async Task<string> ReadAllTextAsync(
        this ITransaction transaction,
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(encoding);
        await using Stream stream = await transaction.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 読み取ったバイトを行の配列にする。エンコーディングは BOM を見て決める
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>改行を含まない行の配列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task<string[]> ReadAllLinesAsync(
        this ITransaction transaction,
        string path,
        CancellationToken cancellationToken = default)
    {
        return ReadAllLinesAsync(transaction, path, Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// 読み取ったバイトを、指定したエンコーディングで行の配列にする。BOM があればそちらを優先する
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="encoding">BOM が無いときのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>改行を含まない行の配列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> または <paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static async Task<string[]> ReadAllLinesAsync(
        this ITransaction transaction,
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(encoding);
        await using Stream stream = await transaction.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        List<string> lines = new List<string>();
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return lines.ToArray();
            }

            lines.Add(line);
        }
    }

    /// <summary>
    /// 文字列を書く。ディスク上にファイルが無ければ Add、あれば Update。エンコーディングは BOM なし UTF-8
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む文字列。null は空として書く</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task WriteAllTextAsync(
        this ITransaction transaction,
        string path,
        string? contents,
        CancellationToken cancellationToken = default)
    {
        return WriteAllTextAsync(transaction, path, contents, _utf8NoBom, cancellationToken);
    }

    /// <summary>
    /// 文字列を、指定したエンコーディングで書く。ディスク上にファイルが無ければ Add、あれば Update
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む文字列。null は空として書く</param>
    /// <param name="encoding">書き込みのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> または <paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task WriteAllTextAsync(
        this ITransaction transaction,
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return WriteEncodedAsync(
            transaction,
            path,
            encoding,
            async (writer, token) =>
            {
                await writer.WriteAsync((contents ?? string.Empty).AsMemory(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// 行を書く。ディスク上にファイルが無ければ Add、あれば Update。エンコーディングは BOM なし UTF-8
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む行。各行の改行は含めない</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> または <paramref name="contents"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task WriteAllLinesAsync(
        this ITransaction transaction,
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default)
    {
        return WriteAllLinesAsync(transaction, path, contents, _utf8NoBom, cancellationToken);
    }

    /// <summary>
    /// 行を、指定したエンコーディングで書く。ディスク上にファイルが無ければ Add、あれば Update
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む行。各行の改行は含めない</param>
    /// <param name="encoding">書き込みのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/>、<paramref name="contents"/>、または <paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    public static Task WriteAllLinesAsync(
        this ITransaction transaction,
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        return WriteEncodedAsync(
            transaction,
            path,
            encoding,
            async (writer, token) =>
            {
                foreach (string line in contents)
                {
                    await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// メモリ上のバイトを、ディスク上のファイル有無で Add または Update する
    /// </summary>
    /// <param name="transaction">対象のトランザクション</param>
    /// <param name="path">対象パス</param>
    /// <param name="content">書き込む内容。位置 0 から読む</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> が null</exception>
    /// <exception cref="ArgumentException">Txfio のトランザクションではない、またはパスがワークフォルダの外である</exception>
    internal static Task StageAsync(
        ITransaction transaction,
        string path,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not Transaction concrete)
        {
            throw new ArgumentException("Txfio のトランザクションである必要があります", nameof(transaction));
        }

        if (concrete.FileExistsOnDisk(path))
        {
            return concrete.UpdateAsync(path, content, progress: null, cancellationToken);
        }

        return concrete.AddAsync(path, content, progress: null, cancellationToken);
    }

    private static async Task WriteEncodedAsync(
        ITransaction transaction,
        string path,
        Encoding encoding,
        Func<StreamWriter, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using MemoryStream buffer = new MemoryStream();
        await using (StreamWriter writer = new StreamWriter(buffer, encoding, bufferSize: 1024, leaveOpen: true))
        {
            await write(writer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await StageAsync(transaction, path, buffer, cancellationToken).ConfigureAwait(false);
    }
}
