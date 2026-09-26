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
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, sourcePath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, destinationPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, sourcePath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, destinationPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, destinationPath);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, sourcePath);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, destinationPath);
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
        if ((index >= 0 && _paths.Rows[index].Kind != PendingChangeKind.CreateDirectory)
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
        await _locks.AcquireSharedAsync(_workFolder).ConfigureAwait(false);
        await _locks.AcquireAsync(_workFolder, sourcePath, destinationPath).ConfigureAwait(false);
        if (!File.Exists(sourcePath))
        {
            throw new ExternalConflictException("コピー元のファイルが存在しません: " + sourcePath, sourcePath);
        }

        if (IsReparsePoint(sourcePath))
        {
            throw new InvalidOperationException("シンボリックリンクはコピーできません: " + sourcePath);
        }

        EnsureCopyDestinationFree(destinationPath);
        int operationCount = _paths.Count;
        string stagingPath = WorkPath.StagingFilePath(destinationPath, _transactionId);
        _paths.Add(new JournalOperation(PendingChangeKind.Add, destinationPath, stagingPath));
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
        await _locks.AcquireSharedAsync(_workFolder).ConfigureAwait(false);
        using PathLockSet.ReadingScope reading = await _locks.AcquireForReadingAsync(
                _workFolder,
                new[] { sourcePath, destinationPath },
                new[] { destinationPath },
                new[] { sourcePath })
            .ConfigureAwait(false);
        if (!Directory.Exists(sourcePath))
        {
            throw new ExternalConflictException("コピー元のディレクトリが存在しません: " + sourcePath, sourcePath);
        }

        EnsureCopyDestinationFree(destinationPath);
        List<string> directories = new List<string> { destinationPath };
        List<PlannedTreeFile> files = new List<PlannedTreeFile>();
        if (!IsReparsePoint(sourcePath))
        {
            PlanCopiedTree(sourcePath, sourcePath, destinationPath, directories, files, cancellationToken);
        }

        await ApplyCopiedTreeAsync(directories, files, progress, cancellationToken)
            .ConfigureAwait(false);
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
        while (_paths.Count > operationCount)
        {
            JournalOperation operation = _paths.Rows[_paths.Count - 1];
            _paths.RemoveAt(_paths.Count - 1);
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
}
