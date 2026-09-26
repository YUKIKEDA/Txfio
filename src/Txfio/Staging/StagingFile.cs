using System.Buffers;

namespace Txfio;

/// <summary>
/// ステージングファイル（`.txnew`）の書き込みと削除
/// </summary>
internal static class StagingFile
{
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// 呼び出し側のストリームを `.txnew` へコピーしてフラッシュする
    /// </summary>
    /// <param name="stagingPath">書き込み先</param>
    /// <param name="content">内容（Dispose しない）</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書いたバイト数</returns>
    internal static async Task<long> WriteAsync(
        string stagingPath,
        Stream content,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        long? totalBytes = TryGetRemaining(content);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                stagingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            long written = await CopyAsync(content, stream, totalBytes, progress, cancellationToken)
                .ConfigureAwait(false);
            await FlushToDiskAsync(stream, cancellationToken).ConfigureAwait(false);
            return written;
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
    /// 排他で開けることを確かめてから、同じディレクトリの移し先へファイルを移し、移し先があれば置き換える
    /// </summary>
    /// <param name="sourcePath">移すファイル</param>
    /// <param name="destinationPath">移し先</param>
    internal static void MoveReplacing(string sourcePath, string destinationPath)
    {
        // 読み取り中は FileShare.Delete で移せるため、排他で開けなければ移さない
        FileStream exclusive = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 1);
        exclusive.Dispose();
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// コミットの適用で、`.txnew` を対象パスへ移す
    /// </summary>
    /// <remarks>
    /// 置き換え（Update）は <see cref="File.Replace(string, string, string?, bool)"/> であり、対象ファイルの ACL、属性、作成日時、代替データストリームを新しいファイルへ移す
    /// それらを移せないときも、中身の置き換えを優先して失敗にしない
    /// </remarks>
    /// <param name="stagingPath">ステージングファイル（`.txnew`）</param>
    /// <param name="targetPath">対象パス</param>
    /// <param name="replace"><see langword="true"/> なら既存のファイルを置き換える（Update）、<see langword="false"/> なら新規に作る（Add）</param>
    internal static void MoveToTarget(string stagingPath, string targetPath, bool replace)
    {
        if (replace)
        {
            File.Replace(stagingPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(stagingPath, targetPath, overwrite: false);
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

    /// <summary>
    /// 呼び出し側のストリームを、まだ存在しないファイルへコピーしてフラッシュする
    /// </summary>
    /// <param name="content">内容（Dispose しない）</param>
    /// <param name="destinationPath">新しいファイルのパス</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書いたバイト数</returns>
    internal static async Task<long> CopyToNewFileAsync(
        Stream content,
        string destinationPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        long? totalBytes = TryGetRemaining(content);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
        }
        catch (IOException exception) when (exception is DirectoryNotFoundException || File.Exists(destinationPath))
        {
            if (exception is DirectoryNotFoundException)
            {
                string parent = System.IO.Path.GetDirectoryName(destinationPath) ?? destinationPath;
                throw new ExternalConflictException("親ディレクトリが存在しません: " + parent, parent);
            }

            throw new ExternalConflictException("コピー先のファイルが既に存在します: " + destinationPath, destinationPath);
        }

        try
        {
            long written = await CopyAsync(content, stream, totalBytes, progress, cancellationToken)
                .ConfigureAwait(false);
            await FlushToDiskAsync(stream, cancellationToken).ConfigureAwait(false);
            return written;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;
            TryDelete(destinationPath);
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
    /// ストリームを別のストリームへコピーし、書き終えたバイト数を通知する
    /// </summary>
    /// <param name="source">コピー元（Dispose しない）</param>
    /// <param name="destination">コピー先（Dispose しない）</param>
    /// <param name="totalBytes">通知に載せる全体のバイト数（分からなければ null）</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>コピーしたバイト数</returns>
    internal static async Task<long> CopyAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long copied = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new TransferProgress(copied, totalBytes));
            }

            if (copied == 0)
            {
                progress?.Report(new TransferProgress(0, totalBytes));
            }

            return copied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 書いた内容をディスクまで書き出す
    /// </summary>
    /// <remarks>
    /// 書き終えたなら 1 回だけ、バッファを出してからディスクまでフラッシュする
    /// </remarks>
    /// <param name="stream">書き終えたストリーム</param>
    /// <param name="cancellationToken">バッファを OS へ出すあいだの取り消し（ディスクまでのフラッシュは取り消せない）</param>
    /// <returns>ディスクまで書き出したこと</returns>
    internal static async Task FlushToDiskAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static long? TryGetRemaining(Stream content)
    {
        if (!content.CanSeek)
        {
            return null;
        }

        try
        {
            long remaining = content.Length - content.Position;
            return remaining >= 0 ? remaining : null;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            return null;
        }
    }
}
