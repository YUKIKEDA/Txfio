using System.IO.Compression;

namespace Txfio;

/// <content>
/// ZIP アーカイブの作成（ワークフォルダ内への Add と、外への書き出し）
/// </content>
internal sealed partial class Transaction
{
    private static readonly DateTime _minimumEntryTime = new DateTime(1980, 1, 1, 0, 0, 0);
    private static readonly DateTime _maximumEntryTime = new DateTime(2107, 12, 31, 23, 59, 58);

    /// <inheritdoc />
    public async Task CreateArchiveAsync(
        string source,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, source);
        string archive = WorkPath.ResolveInWorkFolder(_workFolder, archivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, archive);
        StagingRules.ThrowIfArchiveInsideSource(sourcePath, archive);
        StagingRules.ThrowIfInsideDeleteTree(_operations, sourcePath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, archive);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, sourcePath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, archive);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, archive);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, sourcePath);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, archive);
        ThrowIfCopyPathIsStaged(sourcePath);
        ThrowIfCopyPathIsStaged(archive);
        EnsureCopySourceAvailable(sourcePath);
        EnsureCopyDestinationFree(archive);

        bool isDirectory = Directory.Exists(sourcePath);
        if (isDirectory)
        {
            _locks.AcquireExclusive(_workFolder);
            _locks.RejectForeignLocks(_workFolder);
        }
        else
        {
            _locks.AcquireShared(_workFolder);
        }

        _locks.Acquire(_workFolder, sourcePath, archive);
        if (isDirectory ? !Directory.Exists(sourcePath) : !File.Exists(sourcePath))
        {
            throw new ExternalConflictException("入力が存在しません: " + sourcePath, sourcePath);
        }

        EnsureCopyDestinationFree(archive);
        string stagingPath = WorkPath.StagingFilePath(archive, _transactionId);
        int operationCount = _operations.Count;
        try
        {
            await using (FileStream output = new FileStream(
                stagingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await WriteArchiveAsync(
                        output,
                        sourcePath,
                        isDirectory,
                        compressionLevel,
                        includeBaseDirectory,
                        OpenArchiveSourceFromDisk,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _operations.Add(new JournalOperation(PendingChangeKind.Add, archive, stagingPath));
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            StagingFile.TryDelete(stagingPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ExportArchiveAsync(
        string source,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, source);
        string destinationPath = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalArchivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        bool isDirectory = Directory.Exists(sourcePath);
        if (!isDirectory)
        {
            if (File.Exists(sourcePath) && IsReparsePoint(sourcePath))
            {
                throw new InvalidOperationException("シンボリックリンクは ZIP に入れられません: " + sourcePath);
            }

            // 入力が無いときは、書き出し先より先に知らせる
            await using FileStream probe = OpenExportSource(sourcePath);
        }

        EnsureExportDestination(destinationPath);
        FileStream output = OpenNewExportArchive(destinationPath);
        try
        {
            await using (output.ConfigureAwait(false))
            {
                await WriteArchiveAsync(
                        output,
                        sourcePath,
                        isDirectory,
                        compressionLevel,
                        includeBaseDirectory,
                        OpenExportSource,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            StagingFile.TryDelete(destinationPath);
            throw;
        }
    }

    private static FileStream OpenNewExportArchive(string destinationPath)
    {
        try
        {
            return new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (IOException exception) when (exception is DirectoryNotFoundException || File.Exists(destinationPath))
        {
            if (exception is DirectoryNotFoundException)
            {
                string parent = System.IO.Path.GetDirectoryName(destinationPath) ?? destinationPath;
                throw new ExternalConflictException("親ディレクトリが存在しません: " + parent, parent);
            }

            throw new ExternalConflictException("ZIP のパスに既にファイルがあります: " + destinationPath, destinationPath);
        }
    }

    private static FileStream OpenArchiveSourceFromDisk(string path)
    {
        return OpenExternalFile(path, "入力のファイルが存在しません: " + path, path);
    }

    private static DateTimeOffset ToEntryTime(DateTime lastWriteTime)
    {
        if (lastWriteTime < _minimumEntryTime)
        {
            return _minimumEntryTime;
        }

        if (lastWriteTime > _maximumEntryTime)
        {
            return _maximumEntryTime;
        }

        return lastWriteTime;
    }

    private static async Task WriteEntryAsync(
        ZipArchive zip,
        string entryName,
        FileStream content,
        CompressionLevel compressionLevel,
        DirectoryCopyProgress progress,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, compressionLevel);
        entry.LastWriteTime = ToEntryTime(File.GetLastWriteTime(content.Name));
        long copied;
        Stream entryStream = entry.Open();
        await using (entryStream.ConfigureAwait(false))
        {
            await StagingFile.CopyAsync(content, entryStream, null, progress, cancellationToken)
                .ConfigureAwait(false);
            copied = content.Position;
        }

        progress.CompleteFile(copied);
    }

    private async Task WriteArchiveAsync(
        Stream output,
        string sourcePath,
        bool isDirectory,
        CompressionLevel compressionLevel,
        bool includeBaseDirectory,
        Func<string, FileStream> openFile,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        DirectoryCopyProgress tracker = new DirectoryCopyProgress(progress);
        using (ZipArchive zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (isDirectory)
            {
                string prefix = includeBaseDirectory
                    ? System.IO.Path.GetFileName(sourcePath.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar)) + "/"
                    : string.Empty;
                bool wroteChild = !IsReparsePoint(sourcePath)
                    && await WriteDirectoryEntriesAsync(
                            zip,
                            sourcePath,
                            sourcePath,
                            prefix,
                            compressionLevel,
                            openFile,
                            tracker,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!wroteChild && prefix.Length > 0)
                {
                    zip.CreateEntry(prefix);
                }
            }
            else
            {
                await using FileStream content = openFile(sourcePath);
                await WriteEntryAsync(
                        zip,
                        System.IO.Path.GetFileName(sourcePath),
                        content,
                        compressionLevel,
                        tracker,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!tracker.Reported)
        {
            progress?.Report(new TransferProgress(0, null));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteDirectoryEntriesAsync(
        ZipArchive zip,
        string sourceRoot,
        string current,
        string prefix,
        CompressionLevel compressionLevel,
        Func<string, FileStream> openFile,
        DirectoryCopyProgress progress,
        CancellationToken cancellationToken)
    {
        bool wroteAny = false;
        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry) || WorkPath.IsThisTransactionStagingFile(entry, _transactionId))
            {
                continue;
            }

            string entryName = prefix
                + System.IO.Path.GetRelativePath(sourceRoot, entry)
                    .Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (Directory.Exists(entry))
            {
                bool wroteChild = await WriteDirectoryEntriesAsync(
                        zip,
                        sourceRoot,
                        entry,
                        prefix,
                        compressionLevel,
                        openFile,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!wroteChild)
                {
                    zip.CreateEntry(entryName + "/");
                }

                wroteAny = true;
                continue;
            }

            if (!File.Exists(entry))
            {
                continue;
            }

            await using FileStream content = openFile(entry);
            await WriteEntryAsync(zip, entryName, content, compressionLevel, progress, cancellationToken)
                .ConfigureAwait(false);
            wroteAny = true;
        }

        return wroteAny;
    }
}
