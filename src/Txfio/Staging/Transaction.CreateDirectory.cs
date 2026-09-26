namespace Txfio;

/// <content>
/// 空ディレクトリを呼び出した時点で作る（配下の操作は通常どおりであり、破棄ではそのディレクトリを中身ごと消す）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, targetPath);
        if (FindOperationIndex(targetPath) >= 0 || FindMoveToIndex(targetPath) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, targetPath);
        using PathLockSet.WorkFolderExclusive exclusive = _locks.EnterExclusive(_workFolder);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new ExternalConflictException("作成対象のパスが既に存在します: " + targetPath, targetPath);
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.CreateDirectory,
            targetPath,
            isDirectory: true);
        _paths.Rows.Add(operation);
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Rows.Remove(operation);
            throw;
        }

        try
        {
            Directory.CreateDirectory(targetPath);

            // 作ったあとで作成済みを書く（未作成のまま落ちたときは、Recover が他の作ったディレクトリを中身ごと消さない）
            JournalOperation created = operation.WithDirectoryCreated();
            _paths.Rows[_paths.Rows.IndexOf(operation)] = created;
            operation = created;
            await PersistAsync(committing: false, CancellationToken.None).ConfigureAwait(false);
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

        _paths.Rows.Remove(operation);
        try
        {
            await PersistAsync(committing: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _paths.Rows.Add(operation);
        }
        catch (UnauthorizedAccessException)
        {
            _paths.Rows.Add(operation);
        }
    }
}
