namespace Txfio;

/// <content>
/// 外部プロセスが中へ書く空ディレクトリの作成
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, targetPath);
        StagingRules.ThrowIfInsideCreateDirectory(_operations, targetPath);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, targetPath);
        if (FindOperationIndex(targetPath) >= 0 || FindMoveToIndex(targetPath) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        _locks.AcquireExclusive(_workFolder);
        _locks.RejectForeignLocks(_workFolder);
        _locks.Acquire(_workFolder, targetPath);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new ExternalConflictException("作成対象のパスが既に存在します: " + targetPath, targetPath);
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.CreateDirectory,
            targetPath,
            isDirectory: true);
        _operations.Add(operation);
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _operations.Remove(operation);
            throw;
        }

        try
        {
            Directory.CreateDirectory(targetPath);
        }
        catch
        {
            await RemoveFailedCreateDirectoryAsync(operation).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RemoveFailedCreateDirectoryAsync(JournalOperation operation)
    {
        try
        {
            if (Directory.Exists(operation.Path))
            {
                Directory.Delete(operation.Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // ディレクトリが残っても、ジャーナルに載っていれば破棄で消える
        }
        catch (UnauthorizedAccessException)
        {
            // ディレクトリが残っても、ジャーナルに載っていれば破棄で消える
        }

        _operations.Remove(operation);
        try
        {
            await PersistAsync(committing: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _operations.Add(operation);
        }
        catch (UnauthorizedAccessException)
        {
            _operations.Add(operation);
        }
    }
}
