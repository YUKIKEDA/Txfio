namespace Txfio;

/// <content>
/// ステージング（Add / Update / Delete / DeleteTree / Move）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public Task AddAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Add, path, content, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Update, path, content, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_operations, targetPath);
        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, targetPath);
        StagingRules.EnsureParentDirectoryExists(targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        if (existingIndex >= 0)
        {
            JournalOperation existing = _operations[existingIndex];
            if (existing.Kind == PendingChangeKind.Delete)
            {
                return;
            }

            if (existing.Kind == PendingChangeKind.Move && existing.IsDirectory)
            {
                await FoldDirectoryMoveToDeleteAsync(existingIndex, existing.Path, cancellationToken)
                    .ConfigureAwait(false);
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

        int moveToIndex = FindMoveToIndex(targetPath);
        if (moveToIndex >= 0)
        {
            JournalOperation move = _operations[moveToIndex];
            if (move.IsDirectory)
            {
                await FoldDirectoryMoveToDeleteAsync(moveToIndex, move.Path, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PersistReplacingOperationAsync(
                    moveToIndex,
                    new JournalOperation(PendingChangeKind.Delete, move.Path),
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
    public async Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_operations, targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        if (existingIndex >= 0
            && _operations[existingIndex].Kind == PendingChangeKind.Move
            && _operations[existingIndex].IsDirectory)
        {
            await FoldDirectoryMoveToDeleteTreeAsync(existingIndex, _operations[existingIndex].Path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        int moveToIndex = FindMoveToIndex(targetPath);
        if (moveToIndex >= 0 && _operations[moveToIndex].IsDirectory)
        {
            await FoldDirectoryMoveToDeleteTreeAsync(moveToIndex, _operations[moveToIndex].Path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        StagingRules.ThrowIfOperationUnderDirectory(_operations, targetPath);
        if (existingIndex >= 0)
        {
            if (_operations[existingIndex].Kind == PendingChangeKind.DeleteTree)
            {
                return;
            }

            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        if (moveToIndex >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        if (File.Exists(targetPath))
        {
            throw new UnsupportedOperationException("ファイルの全削除は未対応です: " + targetPath);
        }

        if (!Directory.Exists(targetPath))
        {
            throw new ExternalConflictException("削除対象のディレクトリが存在しません: " + targetPath, targetPath);
        }

        await StageDeleteTreeAsync(targetPath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, oldPath);
        string destPath = WorkPath.ResolveInWorkFolder(_workFolder, newPath);
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("同じパスへは移動できません: " + sourcePath);
        }

        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, destPath);
        StagingRules.EnsureSameVolume(sourcePath, destPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, destPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, sourcePath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, destPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, sourcePath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, destPath);
        StagingRules.ThrowIfCreateDirectoryPath(_operations, sourcePath);
        StagingRules.ThrowIfCreateDirectoryPath(_operations, destPath);

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
        int moveToSource = FindMoveToIndex(sourcePath);
        if (IsDirectoryMove(sourcePath, sourceIndex, moveToSource))
        {
            await MoveDirectoryAsync(sourcePath, destPath, sourceIndex, moveToSource, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (sourceIndex >= 0)
        {
            PendingChangeKind sourceKind = _operations[sourceIndex].Kind;
            if (sourceKind == PendingChangeKind.Delete || sourceKind == PendingChangeKind.DeleteTree)
            {
                throw new InvalidOperationException("削除予約されたパスは移動できません");
            }

            if (sourceKind != PendingChangeKind.Move
                && sourceKind != PendingChangeKind.Add
                && sourceKind != PendingChangeKind.Update)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }

        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, sourcePath, destPath);
        StagingRules.EnsureParentDirectoryExists(destPath);
        StagingRules.EnsureMoveDestinationIsFree(destPath);

        if (sourceIndex >= 0)
        {
            await MovePendingSourceAsync(sourceIndex, destPath, cancellationToken).ConfigureAwait(false);
            return;
        }

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

    private async Task MoveDirectoryAsync(
        string sourcePath,
        string destPath,
        int sourceIndex,
        int moveToSource,
        CancellationToken cancellationToken)
    {
        string root = sourcePath;
        int replaceIndex = -1;
        if (sourceIndex >= 0
            && _operations[sourceIndex].Kind == PendingChangeKind.Move
            && _operations[sourceIndex].IsDirectory)
        {
            root = _operations[sourceIndex].Path;
            replaceIndex = sourceIndex;
        }
        else if (moveToSource >= 0 && _operations[moveToSource].IsDirectory)
        {
            root = _operations[moveToSource].Path;
            replaceIndex = moveToSource;
        }
        else if (sourceIndex >= 0)
        {
            if (_operations[sourceIndex].Kind == PendingChangeKind.Delete
                || _operations[sourceIndex].Kind == PendingChangeKind.DeleteTree)
            {
                throw new InvalidOperationException("削除予約されたパスは移動できません");
            }

            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        StagingRules.ThrowIfDirectoryMoveConflicts(_operations, root, destPath);
        _locks.AcquireExclusive(_workFolder);
        _locks.RejectForeignLocks(_workFolder);
        _locks.Acquire(_workFolder, root, destPath);
        StagingRules.EnsureParentDirectoryExists(destPath);
        StagingRules.EnsureMoveDestinationIsFree(destPath);
        if (!Directory.Exists(root))
        {
            throw new ExternalConflictException("移動元のディレクトリが存在しません: " + root, root);
        }

        if (replaceIndex >= 0)
        {
            await PersistReplacingOperationAsync(
                    replaceIndex,
                    new JournalOperation(
                        PendingChangeKind.Move,
                        root,
                        newPath: destPath,
                        isDirectory: true),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            root,
            newPath: destPath,
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
    }

    private bool IsDirectoryMove(string sourcePath, int sourceIndex, int moveToSource)
    {
        if (Directory.Exists(sourcePath))
        {
            return true;
        }

        if (sourceIndex >= 0
            && _operations[sourceIndex].Kind == PendingChangeKind.Move
            && _operations[sourceIndex].IsDirectory)
        {
            return true;
        }

        return moveToSource >= 0 && _operations[moveToSource].IsDirectory;
    }

    private async Task FoldDirectoryMoveToDeleteAsync(
        int moveIndex,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        StagingRules.EnsureDirectoryDeleteAllowed(directoryPath, _operations, _transactionId);
        await PersistReplacingOperationAsync(
                moveIndex,
                new JournalOperation(PendingChangeKind.Delete, directoryPath, isDirectory: true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FoldDirectoryMoveToDeleteTreeAsync(
        int moveIndex,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        StagingRules.ThrowIfOperationUnderDirectory(_operations, directoryPath);
        await StageDeleteTreeAsync(directoryPath, cancellationToken, moveIndex).ConfigureAwait(false);
    }

    private async Task StageDeleteTreeAsync(
        string directoryPath,
        CancellationToken cancellationToken,
        int replaceIndex = -1)
    {
        _locks.AcquireExclusive(_workFolder);
        _locks.RejectForeignLocks(_workFolder);
        _locks.Acquire(_workFolder, directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new ExternalConflictException("削除対象のディレクトリが存在しません: " + directoryPath, directoryPath);
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.DeleteTree,
            directoryPath,
            isDirectory: true);
        if (replaceIndex >= 0)
        {
            await PersistReplacingOperationAsync(replaceIndex, operation, cancellationToken).ConfigureAwait(false);
            return;
        }

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

    private async Task MovePendingSourceAsync(
        int sourceIndex,
        string destPath,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _operations[sourceIndex];
        if (existing.Kind == PendingChangeKind.Delete || existing.Kind == PendingChangeKind.DeleteTree)
        {
            throw new InvalidOperationException("削除予約されたパスは移動できません");
        }

        if (existing.Kind == PendingChangeKind.Move)
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

    /// <summary>
    /// Move 先への Update を、移動先の Add と元の Delete に畳む
    /// </summary>
    /// <param name="moveIndex">Move 操作のインデックス</param>
    /// <param name="destPath">Update 対象（Move の移動先）</param>
    /// <param name="content">新しい内容</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>畳み込みとジャーナル書き込みの完了</returns>
    private async Task FoldMoveDestinationUpdateAsync(
        int moveIndex,
        string destPath,
        Stream content,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string stagingPath = WorkPath.StagingFilePath(destPath, _transactionId);
        await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);

        JournalOperation[] previous = _operations.ToArray();
        try
        {
            JournalOperation move = _operations[moveIndex];
            _operations.RemoveAt(moveIndex);
            _operations.Add(new JournalOperation(PendingChangeKind.Add, destPath, stagingPath));
            _operations.Add(new JournalOperation(PendingChangeKind.Delete, move.Path));

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _operations.Clear();
            _operations.AddRange(previous);
            StagingFile.TryDelete(stagingPath);
            throw;
        }
    }

    private async Task StageAsync(
        PendingChangeKind kind,
        string path,
        Stream content,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_operations, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_operations, targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        PendingChangeKind recordedKind = kind;
        int moveToIndex = -1;
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
            moveToIndex = FindMoveToIndex(targetPath);
            if (moveToIndex >= 0
                && (kind != PendingChangeKind.Update || _operations[moveToIndex].IsDirectory))
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }

        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, targetPath);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (moveToIndex >= 0)
        {
            await FoldMoveDestinationUpdateAsync(moveToIndex, targetPath, content, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (existingIndex < 0)
        {
            StagingRules.EnsureTargetMatchesKind(kind, targetPath);
        }

        JournalOperation? previous = existingIndex >= 0 ? _operations[existingIndex] : null;
        string stagingPath = previous?.StagingPath
            ?? WorkPath.StagingFilePath(targetPath, _transactionId);
        string? backupPath = null;
        JournalOperation? staged = null;
        bool journalUpdated = false;
        try
        {
            staged = new JournalOperation(recordedKind, targetPath, stagingPath);
            if (existingIndex >= 0)
            {
                _operations[existingIndex] = staged;
            }
            else
            {
                _operations.Add(staged);
            }

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            journalUpdated = true;
            if (previous?.StagingPath is not null
                && string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(stagingPath))
            {
                backupPath = stagingPath + ".prev";
                await StagingFile.CopyAsync(stagingPath, backupPath, cancellationToken).ConfigureAwait(false);
            }

            await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (staged is not null)
            {
                if (existingIndex < 0)
                {
                    _operations.Remove(staged);
                }
                else
                {
                    _operations[existingIndex] = previous!;
                }
            }

            if (backupPath is not null)
            {
                try
                {
                    await StagingFile.CopyAsync(backupPath, stagingPath, CancellationToken.None)
                        .ConfigureAwait(false);
                    StagingFile.TryDelete(backupPath);
                }
                catch (IOException)
                {
                    // 退避の復元に失敗しても、元の例外を投げる
                }
            }
            else if (existingIndex < 0
                || previous?.StagingPath is null
                || !string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase))
            {
                StagingFile.TryDelete(stagingPath);
            }

            if (journalUpdated)
            {
                await TryPersistUndoAsync().ConfigureAwait(false);
            }

            throw;
        }

        StagingFile.TryDelete(backupPath);
        if (previous?.StagingPath is not null
            && !string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase))
        {
            StagingFile.TryDelete(previous.StagingPath);
        }
    }
}
