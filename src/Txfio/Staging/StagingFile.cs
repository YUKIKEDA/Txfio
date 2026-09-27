using System.Buffers;

namespace Txfio;

/// <summary>
/// Writes and deletes staging files (<c>.txnew</c>).
/// </summary>
internal static class StagingFile
{
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Copies the caller's stream to <c>.txnew</c> and flushes it.
    /// </summary>
    /// <param name="stagingPath">The path to write to.</param>
    /// <param name="content">The content (not disposed).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The number of bytes written.</returns>
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
    /// After confirming the file can be opened exclusively, moves it to a destination in the same directory, replacing the destination if it exists.
    /// </summary>
    /// <param name="sourcePath">The file to move.</param>
    /// <param name="destinationPath">The destination.</param>
    internal static void MoveReplacing(string sourcePath, string destinationPath)
    {
        // While it is being read, FileShare.Delete allows a move, so do not move it unless it can be opened exclusively.
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
    /// Moves <c>.txnew</c> to the target path when the commit is applied.
    /// </summary>
    /// <remarks>
    /// A replacement (Update) uses <see cref="File.Replace(string, string, string?, bool)"/>, which moves the target file's ACL, attributes, creation time, and alternate data streams to the new file.
    /// Even when those cannot be moved, replacing the content wins and the operation does not fail.
    /// </remarks>
    /// <param name="stagingPath">The staging file (<c>.txnew</c>).</param>
    /// <param name="targetPath">The target path.</param>
    /// <param name="replace"><see langword="true"/> replaces an existing file (Update); <see langword="false"/> creates a new one (Add).</param>
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
    /// Deletes the staging file, if there is one.
    /// </summary>
    /// <param name="stagingPath">The file to delete.</param>
    internal static void TryDelete(string? stagingPath)
    {
        if (string.IsNullOrEmpty(stagingPath) || !File.Exists(stagingPath))
        {
            return;
        }

        File.Delete(stagingPath);
    }

    /// <summary>
    /// Copies the caller's stream to a file that does not exist yet, and flushes it.
    /// </summary>
    /// <param name="content">The content (not disposed).</param>
    /// <param name="destinationPath">The path of the new file.</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The number of bytes written.</returns>
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
                throw new ExternalConflictException("The parent directory does not exist: " + parent, parent);
            }

            throw new ExternalConflictException("The destination file already exists: " + destinationPath, destinationPath);
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
    /// Copies a stream to another stream, and reports the bytes written.
    /// </summary>
    /// <param name="source">The source (not disposed).</param>
    /// <param name="destination">The destination (not disposed).</param>
    /// <param name="totalBytes">The total bytes to report (<see langword="null"/> if unknown).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The number of bytes copied.</returns>
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
    /// Writes what was written through to the disk.
    /// </summary>
    /// <remarks>
    /// Once writing is done, flushes the buffer and then flushes to disk, once.
    /// </remarks>
    /// <param name="stream">The stream that has been written.</param>
    /// <param name="cancellationToken">Cancels while the buffer is handed to the OS (the flush to disk cannot be canceled).</param>
    /// <returns>A task that completes when the data is on disk.</returns>
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
