namespace Txfio;

/// <content>
/// ステージング（Add / Update / Delete / DeleteTree / Move）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task AddAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await StageAsync(PendingChangeKind.Add, path, content, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        await StageAsync(PendingChangeKind.Update, path, content, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);
        StagingRules.ThrowIfOverwriteMovePath(_paths.Rows, targetPath);
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        if (Directory.Exists(targetPath))
        {
            await _locks.AcquireReservingAsync(_workFolder, new[] { targetPath }, new[] { targetPath }, _lockAttempt).ConfigureAwait(false);
        }
        else
        {
            await _locks.AcquireAsync(_workFolder, new[] { targetPath }, _lockAttempt).ConfigureAwait(false);
        }

        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (!Directory.Exists(targetPath))
        {
            _externalChanges?.NoteRemoval(_paths.Rows, targetPath);
        }

        int existingIndex = FindOperationIndex(targetPath);
        if (IsFileMoveOut(existingIndex))
        {
            int contentIndex = FindContentAfterMoveOut(targetPath, existingIndex);
            if (contentIndex >= 0 && _paths.Rows[contentIndex].Kind == PendingChangeKind.Move)
            {
                // 別の Move で入ってくるファイルを消す
                await PersistFoldedAsync(
                        operations => PathTable.FoldMoveOutToSourceDelete(operations, contentIndex, destination: null),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (contentIndex >= 0)
            {
                existingIndex = contentIndex;
            }
        }

        if (existingIndex >= 0)
        {
            JournalOperation existing = _paths.Rows[existingIndex];
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
            JournalOperation move = _paths.Rows[moveToIndex];
            if (move.IsDirectory)
            {
                await FoldDirectoryMoveToDeleteAsync(moveToIndex, move.Path, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PersistFoldedAsync(
                    operations => PathTable.FoldMoveOutToSourceDelete(operations, moveToIndex, destination: null),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(targetPath))
        {
            StagingRules.EnsureDirectoryDeleteAllowed(targetPath, _paths.Rows, _transactionId);
            JournalOperation directoryDelete = new JournalOperation(
                PendingChangeKind.Delete,
                targetPath,
                isDirectory: true);
            await RecordAsync(() => _paths.Add(directoryDelete), cancellationToken).ConfigureAwait(false);

            return;
        }

        StagingRules.EnsureTargetMatchesKind(PendingChangeKind.Delete, targetPath);
        JournalOperation operation = new JournalOperation(PendingChangeKind.Delete, targetPath);
        await RecordAsync(() => _paths.Add(operation), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);
        StagingRules.ThrowIfOverwriteMovePath(_paths.Rows, targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        if (existingIndex >= 0
            && _paths.Rows[existingIndex].Kind == PendingChangeKind.Move
            && _paths.Rows[existingIndex].IsDirectory)
        {
            await FoldDirectoryMoveToDeleteTreeAsync(existingIndex, _paths.Rows[existingIndex].Path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        int moveToIndex = FindMoveToIndex(targetPath);
        if (moveToIndex >= 0 && _paths.Rows[moveToIndex].IsDirectory)
        {
            await FoldDirectoryMoveToDeleteTreeAsync(moveToIndex, _paths.Rows[moveToIndex].Path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, targetPath);
        if (existingIndex >= 0)
        {
            if (_paths.Rows[existingIndex].Kind == PendingChangeKind.DeleteTree)
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
    public Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        return MoveAsync(oldPath, newPath, overwrite: false, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MoveAsync(string oldPath, string newPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
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
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, sourcePath);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, destPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, sourcePath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, destPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, sourcePath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, destPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, sourcePath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, destPath);
        StagingRules.ThrowIfOverwriteMovePath(_paths.Rows, sourcePath);
        StagingRules.ThrowIfOverwriteMovePath(_paths.Rows, destPath);

        int destIndex = FindOperationIndex(destPath);
        bool destIsMoveSource = destIndex >= 0 && _paths.Rows[destIndex].Kind == PendingChangeKind.Move;
        int sourceIndex = FindOperationIndex(sourcePath);
        int moveToSource = FindMoveToIndex(sourcePath);
        bool directoryMove = IsDirectoryMove(sourcePath, sourceIndex, moveToSource);
        bool replacesDirectory = overwrite && !destIsMoveSource && Directory.Exists(destPath);

        // 置き換えの Move は、移動先のファイルの Delete を畳む。入れ替えは DeleteTree も畳む
        int destDeleteIndex = -1;
        if (overwrite
            && destIndex >= 0
            && ((_paths.Rows[destIndex].Kind == PendingChangeKind.Delete && !_paths.Rows[destIndex].IsDirectory)
                || ((directoryMove || replacesDirectory) && _paths.Rows[destIndex].Kind == PendingChangeKind.DeleteTree)))
        {
            destDeleteIndex = destIndex;
            destIndex = -1;
        }

        if (destIndex >= 0 && (!destIsMoveSource || overwrite))
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        if (IsFileMoveOut(destIndex) && FindLaterOperationIndex(destPath, destIndex) >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        int moveToDest = FindMoveToIndex(destPath);
        if (moveToDest >= 0)
        {
            if (string.Equals(_paths.Rows[moveToDest].Path, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        if (directoryMove)
        {
            if (overwrite)
            {
                await ReplaceDirectoryAsync(sourcePath, destPath, sourceIndex, moveToSource, destDeleteIndex, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await MoveDirectoryAsync(
                    sourcePath,
                    destPath,
                    sourceIndex,
                    moveToSource,
                    destIsMoveSource,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (IsFileMoveOut(sourceIndex))
        {
            // 移動済みの元から動かすのは、そのあと元のパスに来た中身である
            int laterIndex = FindLaterOperationIndex(sourcePath, sourceIndex);
            if (laterIndex >= 0)
            {
                sourceIndex = laterIndex;
            }
            else if (moveToSource >= 0)
            {
                sourceIndex = -1;
            }
            else
            {
                throw new ExternalConflictException("移動元のファイルが存在しません: " + sourcePath, sourcePath);
            }
        }

        if (sourceIndex >= 0)
        {
            PendingChangeKind sourceKind = _paths.Rows[sourceIndex].Kind;
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

        // ファイルでディレクトリを入れ替えるときは、移動先の配下を排他の意図ロックで終わりまで予約する
        if (replacesDirectory)
        {
            StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, destPath);
            if (sourceIndex >= 0 || moveToSource >= 0)
            {
                throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
            }
        }

        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        if (replacesDirectory)
        {
            await _locks.AcquireReservingAsync(_workFolder, new[] { sourcePath, destPath }, new[] { sourcePath, destPath }, _lockAttempt).ConfigureAwait(false);
        }
        else
        {
            await _locks.AcquireAsync(_workFolder, new[] { sourcePath, destPath }, _lockAttempt).ConfigureAwait(false);
        }

        StagingRules.EnsureParentDirectoryExists(destPath);
        _externalChanges?.NoteRemoval(_paths.Rows, sourcePath);
        bool replaces = false;
        string? backupPath = null;
        if (!destIsMoveSource)
        {
            if (overwrite && File.Exists(destPath))
            {
                replaces = true;
            }
            else if (replacesDirectory)
            {
                replaces = true;
                backupPath = WorkPath.ReplacedDirectoryPath(destPath, _transactionId);
            }
            else
            {
                StagingRules.EnsureMoveDestinationIsFree(destPath);
            }
        }

        if (sourceIndex < 0 && moveToSource < 0)
        {
            StagingRules.EnsureMoveSourceExists(sourcePath);
        }

        // 畳んだ Delete は、この Move と同じジャーナルの書き込みで外す
        JournalOperation? foldedDelete = null;
        if (destDeleteIndex >= 0)
        {
            foldedDelete = _paths.Rows[destDeleteIndex];
            _paths.RemoveAt(destDeleteIndex);
            sourceIndex = sourceIndex > destDeleteIndex ? sourceIndex - 1 : sourceIndex;
            moveToSource = moveToSource > destDeleteIndex ? moveToSource - 1 : moveToSource;
        }

        try
        {
            await RecordFileMoveAsync(sourcePath, destPath, sourceIndex, moveToSource, replaces, backupPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (foldedDelete is not null)
            {
                _paths.Insert(Math.Min(destDeleteIndex, _paths.Rows.Count), foldedDelete);
                await TryPersistUndoAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task RecordFileMoveAsync(
        string sourcePath,
        string destPath,
        int sourceIndex,
        int moveToSource,
        bool replaces,
        string? backupPath,
        CancellationToken cancellationToken)
    {
        if (sourceIndex >= 0)
        {
            await MovePendingSourceAsync(sourceIndex, destPath, replaces, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (moveToSource >= 0)
        {
            JournalOperation existing = _paths.Rows[moveToSource];
            JournalOperation folded = new JournalOperation(
                PendingChangeKind.Move,
                existing.Path,
                stagingPath: null,
                destPath,
                overwrite: replaces);
            ThrowIfMoveChainCloses(folded, moveToSource);
            await PersistReplacingOperationAsync(moveToSource, folded, cancellationToken).ConfigureAwait(false);
            return;
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            sourcePath,
            stagingPath: backupPath,
            destPath,
            overwrite: replaces);
        ThrowIfMoveChainCloses(operation, replaceIndex: -1);
        await RecordAsync(() => _paths.Add(operation), cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistReplacingOperationAsync(
        int existingIndex,
        JournalOperation? replacement,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _paths.Rows[existingIndex];
        await RecordAsync(
                () =>
                {
                    if (replacement is null)
                    {
                        _paths.RemoveAt(existingIndex);
                    }
                    else
                    {
                        _paths.Set(existingIndex, replacement);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        StagingFile.TryDelete(existing.StagingPath);
    }

    // ディレクトリの入れ替え（移動先の既存ディレクトリを、移動元で中身ごと入れ替える）
    private async Task ReplaceDirectoryAsync(
        string sourcePath,
        string destPath,
        int sourceIndex,
        int moveToSource,
        int destDeleteTreeIndex,
        CancellationToken cancellationToken)
    {
        if (sourceIndex >= 0 || moveToSource >= 0)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        StagingRules.ThrowIfDirectoryReplaceConflicts(_paths.Rows, _createdDirectories, sourcePath, destPath);
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { sourcePath, destPath }, new[] { sourcePath, destPath }, _lockAttempt).ConfigureAwait(false);
        StagingRules.EnsureParentDirectoryExists(destPath);
        if (!Directory.Exists(sourcePath))
        {
            throw new ExternalConflictException("移動元のディレクトリが存在しません: " + sourcePath, sourcePath);
        }

        // 移動先がファイルでもディレクトリでも入れ替える。無ければ普通のディレクトリ Move と同じ
        bool replaces = Directory.Exists(destPath) || File.Exists(destPath);

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            sourcePath,
            stagingPath: replaces ? WorkPath.ReplacedDirectoryPath(destPath, _transactionId) : null,
            newPath: destPath,
            isDirectory: true,
            overwrite: replaces);
        ThrowIfMoveChainCloses(operation, replaceIndex: -1);

        // 畳んだ DeleteTree は、この入れ替えと同じジャーナルの書き込みで外す
        JournalOperation? foldedDelete = null;
        if (destDeleteTreeIndex >= 0)
        {
            foldedDelete = _paths.Rows[destDeleteTreeIndex];
            _paths.RemoveAt(destDeleteTreeIndex);
        }

        _paths.Add(operation);
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Remove(operation);
            if (foldedDelete is not null)
            {
                _paths.Insert(Math.Min(destDeleteTreeIndex, _paths.Rows.Count), foldedDelete);
            }

            throw;
        }
    }

    private async Task MoveDirectoryAsync(
        string sourcePath,
        string destPath,
        int sourceIndex,
        int moveToSource,
        bool destIsMoveSource,
        CancellationToken cancellationToken)
    {
        string root = sourcePath;
        int replaceIndex = -1;
        if (sourceIndex >= 0
            && _paths.Rows[sourceIndex].Kind == PendingChangeKind.Move
            && _paths.Rows[sourceIndex].IsDirectory)
        {
            root = _paths.Rows[sourceIndex].Path;
            replaceIndex = sourceIndex;
        }
        else if (moveToSource >= 0 && _paths.Rows[moveToSource].IsDirectory)
        {
            root = _paths.Rows[moveToSource].Path;
            replaceIndex = moveToSource;
        }
        else if (sourceIndex >= 0)
        {
            if (_paths.Rows[sourceIndex].Kind == PendingChangeKind.Delete
                || _paths.Rows[sourceIndex].Kind == PendingChangeKind.DeleteTree)
            {
                throw new InvalidOperationException("削除予約されたパスは移動できません");
            }

            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        StagingRules.ThrowIfDirectoryMoveConflicts(_paths.Rows, root, destPath);
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { root, destPath }, new[] { root, destPath }, _lockAttempt).ConfigureAwait(false);
        StagingRules.EnsureParentDirectoryExists(destPath);
        if (!destIsMoveSource)
        {
            StagingRules.EnsureMoveDestinationIsFree(destPath);
        }

        if (!Directory.Exists(root))
        {
            throw new ExternalConflictException("移動元のディレクトリが存在しません: " + root, root);
        }

        if (replaceIndex >= 0)
        {
            JournalOperation folded = new JournalOperation(
                PendingChangeKind.Move,
                root,
                newPath: destPath,
                isDirectory: true);
            ThrowIfMoveChainCloses(folded, replaceIndex);
            await PersistReplacingOperationAsync(replaceIndex, folded, cancellationToken).ConfigureAwait(false);
            return;
        }

        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            root,
            newPath: destPath,
            isDirectory: true);
        ThrowIfMoveChainCloses(operation, replaceIndex: -1);
        await RecordAsync(() => _paths.Add(operation), cancellationToken).ConfigureAwait(false);
    }

    private bool IsDirectoryMove(string sourcePath, int sourceIndex, int moveToSource)
    {
        if (Directory.Exists(sourcePath))
        {
            return true;
        }

        if (sourceIndex >= 0
            && _paths.Rows[sourceIndex].Kind == PendingChangeKind.Move
            && _paths.Rows[sourceIndex].IsDirectory)
        {
            return true;
        }

        return moveToSource >= 0 && _paths.Rows[moveToSource].IsDirectory;
    }

    private async Task FoldDirectoryMoveToDeleteAsync(
        int moveIndex,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        StagingRules.EnsureDirectoryDeleteAllowed(directoryPath, _paths.Rows, _transactionId);
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
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, directoryPath);
        await StageDeleteTreeAsync(directoryPath, cancellationToken, moveIndex).ConfigureAwait(false);
    }

    private async Task StageDeleteTreeAsync(
        string directoryPath,
        CancellationToken cancellationToken,
        int replaceIndex = -1)
    {
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { directoryPath }, new[] { directoryPath }, _lockAttempt).ConfigureAwait(false);
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

        await RecordAsync(() => _paths.Add(operation), cancellationToken).ConfigureAwait(false);
    }

    private async Task MovePendingSourceAsync(
        int sourceIndex,
        string destPath,
        bool replaces,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _paths.Rows[sourceIndex];
        if (existing.Kind == PendingChangeKind.Delete || existing.Kind == PendingChangeKind.DeleteTree)
        {
            throw new InvalidOperationException("削除予約されたパスは移動できません");
        }

        if (existing.Kind == PendingChangeKind.Move)
        {
            JournalOperation folded = new JournalOperation(
                PendingChangeKind.Move,
                existing.Path,
                stagingPath: null,
                destPath,
                overwrite: replaces);
            ThrowIfMoveChainCloses(folded, sourceIndex);
            await PersistReplacingOperationAsync(sourceIndex, folded, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (existing.Kind != PendingChangeKind.Add && existing.Kind != PendingChangeKind.Update)
        {
            throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
        }

        // 置き換える先がファイルなら、書き直した中身はその Update になる
        await RetargetStagedContentAsync(
                sourceIndex,
                destPath,
                deleteSource: existing.Kind == PendingChangeKind.Update,
                replaces ? PendingChangeKind.Update : PendingChangeKind.Add,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RetargetStagedContentAsync(
        int sourceIndex,
        string destPath,
        bool deleteSource,
        PendingChangeKind retargetedKind,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _paths.Rows[sourceIndex];
        JournalOperation[] previous = _paths.ToArray();
        string sourcePath = existing.Path;

        // .txnew は移動先の名前に付け替える（元の名前のままだと、移動元へ次に書いたときに同じ .txnew を上書きする）
        string? stagingPath = existing.StagingPath is null
            ? null
            : WorkPath.StagingFilePath(destPath, _transactionId);
        JournalOperation retargeted = new JournalOperation(PendingChangeKind.Add, destPath, stagingPath);
        bool journalUpdated = false;
        bool moved = false;
        try
        {
            if (existing.StagingPath is not null)
            {
                // 付け替えの前後どちらで落ちても、両方の .txnew がジャーナルに載っているようにする
                _paths.Add(retargeted);
                await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
                journalUpdated = true;
                File.Move(existing.StagingPath, stagingPath!);
                moved = true;
                _paths.Remove(retargeted);
            }

            _paths.RemoveAt(sourceIndex);
            _paths.Add(new JournalOperation(retargetedKind, destPath, stagingPath));
            if (deleteSource)
            {
                _paths.Add(new JournalOperation(PendingChangeKind.Delete, sourcePath));
            }

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Load(previous);
            if (moved)
            {
                try
                {
                    File.Move(stagingPath!, existing.StagingPath!);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 戻せなくても、両方の .txnew が載ったジャーナルが残れば次の Recover が消す
                    journalUpdated = false;
                }
            }

            if (journalUpdated)
            {
                await TryPersistUndoAsync().ConfigureAwait(false);
            }

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
        List<JournalOperation> folded = new List<JournalOperation>(_paths.Rows);
        PathTable.FoldMoveOutToSourceDelete(
            folded,
            moveIndex,
            new JournalOperation(PendingChangeKind.Add, destPath, stagingPath));

        // 落ちても Recover が .txnew を消せるよう、書く前にジャーナルへ載せる
        JournalOperation[] previous = _paths.ToArray();
        bool journalUpdated = false;
        try
        {
            _paths.Load(folded);
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            journalUpdated = true;
            await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Load(previous);
            StagingFile.TryDelete(stagingPath);
            if (journalUpdated)
            {
                await TryPersistUndoAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 畳んだ操作一覧をジャーナルに書く（失敗したら元の一覧に戻す）
    /// </summary>
    /// <param name="fold">操作一覧の写しを畳む処理（使い方の誤りなら書く前に例外を投げる）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ジャーナル書き込みの完了</returns>
    private async Task PersistFoldedAsync(
        Action<List<JournalOperation>> fold,
        CancellationToken cancellationToken)
    {
        List<JournalOperation> folded = new List<JournalOperation>(_paths.Rows);
        fold(folded);
        await RecordAsync(() => _paths.Load(folded), cancellationToken).ConfigureAwait(false);
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
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);
        StagingRules.ThrowIfOverwriteMovePath(_paths.Rows, targetPath);

        _paths.PlanStageFile(
            targetPath,
            kind,
            out int existingIndex,
            out int moveToIndex,
            out bool addOntoFileMove,
            out PendingChangeKind recordedKind);

        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireAsync(_workFolder, new[] { targetPath }, _lockAttempt).ConfigureAwait(false);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (kind == PendingChangeKind.Update)
        {
            _externalChanges?.NoteUpdate(_paths.Rows, targetPath);
        }

        if (moveToIndex >= 0)
        {
            string sourcePath = _paths.Rows[moveToIndex].Path;
            await FoldMoveDestinationUpdateAsync(moveToIndex, targetPath, content, progress, cancellationToken)
                .ConfigureAwait(false);
            if (kind == PendingChangeKind.Update)
            {
                _externalChanges?.NoteFoldedUpdate(targetPath, sourcePath);
            }

            return;
        }

        if (existingIndex < 0 && !addOntoFileMove)
        {
            StagingRules.EnsureTargetMatchesKind(kind, targetPath);
        }

        JournalOperation? previous = existingIndex >= 0 ? _paths.Rows[existingIndex] : null;
        string stagingPath = previous?.StagingPath
            ?? WorkPath.StagingFilePath(targetPath, _transactionId);
        string? backupPath = null;
        JournalOperation staged = new JournalOperation(recordedKind, targetPath, stagingPath);
        bool restagesSameFile = previous?.StagingPath is not null
            && string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase);
        await RecordThenMaterializeAsync(
                () =>
                {
                    if (existingIndex >= 0)
                    {
                        _paths.Set(existingIndex, staged);
                    }
                    else
                    {
                        _paths.Add(staged);
                    }
                },
                async () =>
                {
                    // 同じ .txnew を書き直すときは、失敗したら戻せるよう退避しておく
                    if (restagesSameFile && File.Exists(stagingPath))
                    {
                        backupPath = WorkPath.StagingBackupPath(stagingPath);
                        StagingFile.MoveReplacing(stagingPath, backupPath);
                    }

                    await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);
                },
                () =>
                {
                    if (backupPath is not null)
                    {
                        StagingFile.MoveReplacing(backupPath, stagingPath);
                    }
                    else if (!restagesSameFile)
                    {
                        StagingFile.TryDelete(stagingPath);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        StagingFile.TryDelete(backupPath);
        if (previous?.StagingPath is not null
            && !string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase))
        {
            StagingFile.TryDelete(previous.StagingPath);
        }
    }
}
