namespace Txfio;

/// <summary>
/// `.txnew` サイドカーの書き込みと削除
/// </summary>
internal static class StagingFile
{
    /// <summary>
    /// 呼び出し側のストリームを `.txnew` へコピーしてフラッシュする
    /// </summary>
    /// <param name="stagingPath">書き込み先</param>
    /// <param name="content">内容（Dispose しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き込みの完了</returns>
    internal static async Task WriteAsync(string stagingPath, Stream content, CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                stagingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
            }

            TryDelete(stagingPath);
            throw;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// ステージングファイルがあれば削除する
    /// </summary>
    /// <param name="stagingPath">削除対象</param>
    internal static void TryDelete(string stagingPath)
    {
        if (File.Exists(stagingPath))
        {
            File.Delete(stagingPath);
        }
    }
}
