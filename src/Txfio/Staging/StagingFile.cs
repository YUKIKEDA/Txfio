namespace Txfio;

/// <summary>
/// ステージングファイル（`.txnew`）の書き込みと削除
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
    /// ステージングファイルを別パスへコピーしてフラッシュする
    /// </summary>
    /// <param name="sourcePath">コピー元</param>
    /// <param name="destinationPath">コピー先</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>コピーの完了</returns>
    internal static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        FileStream? source = null;
        FileStream? destination = null;
        try
        {
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (destination is not null)
            {
                await destination.DisposeAsync().ConfigureAwait(false);
                destination = null;
            }

            TryDelete(destinationPath);
            throw;
        }
        finally
        {
            if (destination is not null)
            {
                await destination.DisposeAsync().ConfigureAwait(false);
            }

            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// ステージングファイルがあれば削除する
    /// </summary>
    /// <param name="stagingPath">削除対象</param>
    internal static void TryDelete(string? stagingPath)
    {
        if (string.IsNullOrEmpty(stagingPath) || !File.Exists(stagingPath))
        {
            return;
        }

        File.Delete(stagingPath);
    }
}
