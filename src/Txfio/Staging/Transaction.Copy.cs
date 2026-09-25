namespace Txfio;

/// <content>
/// ワークフォルダ内のコピー
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task CopyAsync(
        string source,
        string destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, source);
        string destinationPath = WorkPath.ResolveInWorkFolder(_workFolder, destination);
        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, destinationPath);
        StagingRules.ThrowIfCopyDestinationInsideSource(sourcePath, destinationPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, sourcePath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, destinationPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, sourcePath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, destinationPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, destinationPath);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, sourcePath);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, destinationPath);
        ThrowIfCopyPathIsStaged(sourcePath);
        ThrowIfCopyPathIsStaged(destinationPath);
        EnsureCopySourceAvailable(sourcePath);
        EnsureCopyDestinationFree(destinationPath);

        if (Directory.Exists(sourcePath))
        {
            await CopyDirectoryAsync(sourcePath, destinationPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await CopyFileAsync(sourcePath, destinationPath, progress, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static int DirectoryDepth(string path)
    {
        int depth = 0;
        foreach (char character in path)
        {
            if (character == System.IO.Path.DirectorySeparatorChar
                || character == System.IO.Path.AltDirectorySeparatorChar)
            {
                depth++;
            }
        }

        return depth;
    }

    private static void EnsureCopySourceAvailable(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new ExternalConflictException("コピー元が存在しません: " + sourcePath, sourcePath);
        }

        if (IsReparsePoint(sourcePath))
        {
            throw new InvalidOperationException("シンボリックリンクはコピーできません: " + sourcePath);
        }
    }

    private static void EnsureCopyDestinationFree(string destinationPath)
    {
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            throw new ExternalConflictException("コピー先が既に存在します: " + destinationPath, destinationPath);
        }

        StagingRules.EnsureParentDirectoryExists(destinationPath);
    }

    private void ThrowIfCopyPathIsStaged(string path)
    {
        int index = FindOperationIndex(path);
        if ((index >= 0 && _operations[index].Kind != PendingChangeKind.CreateDirectory)
            || FindMoveToIndex(path) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }
    }

    private async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, sourcePath, destinationPath);
        if (!File.Exists(sourcePath))
        {
            throw new ExternalConflictException("コピー元のファイルが存在しません: " + sourcePath, sourcePath);
        }

        if (IsReparsePoint(sourcePath))
        {
            throw new InvalidOperationException("シンボリックリンクはコピーできません: " + sourcePath);
        }

        EnsureCopyDestinationFree(destinationPath);
        int operationCount = _operations.Count;
        string stagingPath = WorkPath.StagingFilePath(destinationPath, _transactionId);
        _operations.Add(new JournalOperation(PendingChangeKind.Add, destinationPath, stagingPath));
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            await WriteCopySourceAsync(sourcePath, stagingPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            await TryPersistUndoAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CopyDirectoryAsync(
        string sourcePath,
        string destinationPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        _locks.AcquireShared(_workFolder);
        _locks.AcquireReserving(_workFolder, new[] { sourcePath, destinationPath }, destinationPath);
        using PathLockSet.WorkFolderExclusive exclusive = _locks.EnterExclusive(_workFolder);
        if (!Directory.Exists(sourcePath))
        {
            throw new ExternalConflictException("コピー元のディレクトリが存在しません: " + sourcePath, sourcePath);
        }

        EnsureCopyDestinationFree(destinationPath);
        List<string> directories = new List<string> { destinationPath };
        List<PlannedCopyFile> files = new List<PlannedCopyFile>();
        if (!IsReparsePoint(sourcePath))
        {
            PlanDirectoryEntries(sourcePath, sourcePath, destinationPath, directories, files, cancellationToken);
        }

        await ApplyPlannedDirectoryCopyAsync(directories, files, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private void PlanDirectoryEntries(
        string sourceRoot,
        string current,
        string destinationRoot,
        List<string> directories,
        List<PlannedCopyFile> files,
        CancellationToken cancellationToken)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry) || WorkPath.IsThisTransactionStagingFile(entry, _transactionId))
            {
                continue;
            }

            string relative = System.IO.Path.GetRelativePath(sourceRoot, entry);
            string destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(destinationRoot, relative));
            if (Directory.Exists(entry))
            {
                directories.Add(destination);
                PlanDirectoryEntries(sourceRoot, entry, destinationRoot, directories, files, cancellationToken);
                continue;
            }

            if (File.Exists(entry))
            {
                files.Add(new PlannedCopyFile(entry, destination));
            }
        }
    }

    private async Task ApplyPlannedDirectoryCopyAsync(
        IReadOnlyList<string> directories,
        IReadOnlyList<PlannedCopyFile> files,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        int operationCount = _operations.Count;
        int directoryCount = _createdDirectories.Count;
        foreach (string directory in directories)
        {
            _createdDirectories.Add(directory);
        }

        foreach (PlannedCopyFile file in files)
        {
            string stagingPath = WorkPath.StagingFilePath(file.DestinationPath, _transactionId);
            _operations.Add(new JournalOperation(PendingChangeKind.Add, file.DestinationPath, stagingPath));
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            foreach (string directory in directories)
            {
                Directory.CreateDirectory(directory);
            }

            DirectoryCopyProgress tracker = new DirectoryCopyProgress(progress);
            foreach (PlannedCopyFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long bytes = await WriteCopySourceAsync(
                        file.SourcePath,
                        WorkPath.StagingFilePath(file.DestinationPath, _transactionId),
                        tracker,
                        cancellationToken)
                    .ConfigureAwait(false);
                tracker.CompleteFile(bytes);
            }

            if (!tracker.Reported)
            {
                progress?.Report(new TransferProgress(0, null));
            }
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            DeleteCreatedDirectoriesFrom(directoryCount, ignoreIoFailures: true);
            await TryPersistUndoAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> WriteCopySourceAsync(
        string sourcePath,
        string stagingPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await StagingFile.WriteAsync(stagingPath, source, progress, cancellationToken).ConfigureAwait(false);
        return new FileInfo(stagingPath).Length;
    }

    private void RollbackAddedOperations(int operationCount)
    {
        while (_operations.Count > operationCount)
        {
            JournalOperation operation = _operations[_operations.Count - 1];
            _operations.RemoveAt(_operations.Count - 1);
            StagingFile.TryDelete(operation.StagingPath);
        }
    }

    private bool DeleteCreatedDirectoriesFrom(int start, bool ignoreIoFailures)
    {
        if (start < 0 || start >= _createdDirectories.Count)
        {
            return true;
        }

        List<string> pending = _createdDirectories.GetRange(start, _createdDirectories.Count - start);
        pending.Sort(static (left, right) =>
        {
            int byDepth = DirectoryDepth(right).CompareTo(DirectoryDepth(left));
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        });

        bool succeeded = true;
        foreach (string path in pending)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }

                _createdDirectories.Remove(path);
            }
            catch (IOException) when (ignoreIoFailures)
            {
                // 失敗したコピーの後始末では、元の例外を残す
                succeeded = false;
            }
            catch (UnauthorizedAccessException) when (ignoreIoFailures)
            {
                // 失敗したコピーの後始末では、元の例外を残す
                succeeded = false;
            }
        }

        return succeeded;
    }

    private sealed class PlannedCopyFile
    {
        internal PlannedCopyFile(string sourcePath, string destinationPath)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
        }

        internal string SourcePath { get; }

        internal string DestinationPath { get; }
    }

    private sealed class DirectoryCopyProgress : IProgress<TransferProgress>
    {
        private readonly IProgress<TransferProgress>? _inner;
        private long _completed;

        internal DirectoryCopyProgress(IProgress<TransferProgress>? inner)
        {
            _inner = inner;
        }

        internal bool Reported { get; private set; }

        /// <inheritdoc />
        public void Report(TransferProgress value)
        {
            Reported = true;
            _inner?.Report(new TransferProgress(_completed + value.BytesCopied, null));
        }

        internal void CompleteFile(long bytes)
        {
            _completed += bytes;
        }
    }
}
