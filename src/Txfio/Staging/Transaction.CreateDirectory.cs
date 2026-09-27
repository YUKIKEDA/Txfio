namespace Txfio;

/// <content>
/// Creates an empty directory when called (operations under it work as usual, and discard deletes the directory with its contents).
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
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
            throw new InvalidOperationException("This path is already staged by another operation");
        }

        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireAsync(_workFolder, new[] { targetPath }, _lockAttempt).ConfigureAwait(false);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new ExternalConflictException("The path to create already exists: " + targetPath, targetPath);
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.CreateDirectory,
            targetPath,
            isDirectory: true);
        await RecordAsync(() => _paths.Add(operation), cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(targetPath);

            // Record "created" after creating it (if it crashes while still recorded as not created, Recover does not delete, with its contents, a directory of the same name that someone else created).
            JournalOperation created = operation.WithDirectoryCreated();
            _paths.Set(_paths.IndexOf(operation), created);
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
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            // Even if the directory remains, discard deletes it because it is in the journal.
        }

        _paths.Remove(operation);
        try
        {
            await PersistAsync(committing: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            _paths.Add(operation);
        }
    }
}
