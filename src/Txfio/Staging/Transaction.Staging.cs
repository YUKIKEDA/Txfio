namespace Txfio;

/// <content>
/// ステージング（Add / Update / Delete / Move / Attach）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public Task AddAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Add, path, content, cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Update, path, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.EnsureParentDirectoryExists(targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        if (existingIndex >= 0)
        {
            JournalOperation existing = _operations[existingIndex];
            if (existing.Kind == PendingChangeKind.Delete)
            {
                return;
            }

            if (Directory.Exists(targetPath) || existing.IsDirectory)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }

            if (existing.Kind == PendingChangeKind.Add)
            {
                await PersistReplacingOperationAsync(existingIndex, replacement: null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PersistReplacingOperationAsync(
                    existingIndex,
                    new JournalOperation(PendingChangeKind.Delete, targetPath),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(targetPath))
        {
            StagingRules.EnsureDirectoryDeleteAllowed(targetPath, _operations, _transactionId);
            JournalOperation directoryDelete = new JournalOperation(
                PendingChangeKind.Delete,
                targetPath,
                stagingPath: null,
                newPath: null,
                expectedLength: null,
                expectedLastWriteTimeUtc: null,
                isDirectory: true);
            _operations.Add(directoryDelete);
            try
            {
                await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _operations.Remove(directoryDelete);
                throw;
            }

            return;
        }

        StagingRules.EnsureTargetMatchesKind(PendingChangeKind.Delete, targetPath);
        JournalOperation operation = new JournalOperation(PendingChangeKind.Delete, targetPath);
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
    }

    /// <inheritdoc />
    public async Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, oldPath);
        string destPath = WorkPath.ResolveInWorkFolder(_workFolder, newPath);
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StagingRules.EnsureParentDirectoryExists(destPath);
        StagingRules.EnsureSameVolume(sourcePath, destPath);
        StagingRules.EnsureMoveDestinationIsFree(destPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, destPath);

        if (FindOperationIndex(destPath) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        int moveToDest = FindMoveToIndex(destPath);
        if (moveToDest >= 0)
        {
            if (string.Equals(_operations[moveToDest].Path, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        int sourceIndex = FindOperationIndex(sourcePath);
        if (sourceIndex >= 0)
        {
            await MovePendingSourceAsync(sourceIndex, destPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        int moveToSource = FindMoveToIndex(sourcePath);
        if (moveToSource >= 0)
        {
            JournalOperation existing = _operations[moveToSource];
            await PersistReplacingOperationAsync(
                    moveToSource,
                    new JournalOperation(PendingChangeKind.Move, existing.Path, stagingPath: null, destPath),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        StagingRules.EnsureMoveSourceExists(sourcePath);
        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            sourcePath,
            stagingPath: null,
            destPath);
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
    }

    /// <inheritdoc />
    public async Task AttachAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, targetPath);

        if (FindOperationIndex(targetPath) >= 0 || FindMoveToIndex(targetPath) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        StagingRules.EnsureAttachTarget(targetPath);
        FileInfo info = new FileInfo(targetPath);
        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Attach,
            targetPath,
            stagingPath: null,
            newPath: null,
            info.Length,
            info.LastWriteTimeUtc);
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
    }

    private async Task PersistReplacingOperationAsync(
        int existingIndex,
        JournalOperation? replacement,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _operations[existingIndex];
        if (replacement is null)
        {
            _operations.RemoveAt(existingIndex);
        }
        else
        {
            _operations[existingIndex] = replacement;
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (replacement is null)
            {
                _operations.Insert(existingIndex, existing);
            }
            else
            {
                _operations[existingIndex] = existing;
            }

            throw;
        }

        StagingFile.TryDelete(existing.StagingPath);
    }

    private async Task MovePendingSourceAsync(
        int sourceIndex,
        string destPath,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _operations[sourceIndex];
        if (existing.Kind == PendingChangeKind.Delete)
        {
            throw new InvalidOperationException("削除予約されたパスは移動できません");
        }

        if (existing.Kind == PendingChangeKind.Move || existing.Kind == PendingChangeKind.Attach)
        {
            await PersistReplacingOperationAsync(
                    sourceIndex,
                    new JournalOperation(PendingChangeKind.Move, existing.Path, stagingPath: null, destPath),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (existing.Kind != PendingChangeKind.Add && existing.Kind != PendingChangeKind.Update)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        await RetargetStagedContentAsync(
                sourceIndex,
                destPath,
                deleteSource: existing.Kind == PendingChangeKind.Update,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RetargetStagedContentAsync(
        int sourceIndex,
        string destPath,
        bool deleteSource,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _operations[sourceIndex];
        JournalOperation[] previous = _operations.ToArray();
        try
        {
            string sourcePath = existing.Path;
            _operations.RemoveAt(sourceIndex);
            _operations.Add(new JournalOperation(PendingChangeKind.Add, destPath, existing.StagingPath));
            if (deleteSource)
            {
                _operations.Add(new JournalOperation(PendingChangeKind.Delete, sourcePath));
            }

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _operations.Clear();
            _operations.AddRange(previous);
            throw;
        }
    }

    private async Task StageAsync(
        PendingChangeKind kind,
        string path,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        PendingChangeKind recordedKind = kind;
        if (existingIndex >= 0)
        {
            if (_operations[existingIndex].IsDirectory)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }

            recordedKind = StagingRules.NormalizeRestageKind(_operations[existingIndex].Kind, kind);
        }
        else
        {
            if (FindMoveToIndex(targetPath) >= 0)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }

            StagingRules.EnsureTargetMatchesKind(kind, targetPath);
        }

        string stagingPath = WorkPath.StagingFilePath(targetPath, _transactionId);
        await StagingFile.WriteAsync(stagingPath, content, cancellationToken).ConfigureAwait(false);

        JournalOperation operation = new JournalOperation(recordedKind, targetPath, stagingPath);
        if (existingIndex >= 0)
        {
            _operations[existingIndex] = operation;
        }
        else
        {
            _operations.Add(operation);
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (existingIndex < 0)
            {
                _operations.Remove(operation);
                StagingFile.TryDelete(stagingPath);
            }

            throw;
        }
    }
}
