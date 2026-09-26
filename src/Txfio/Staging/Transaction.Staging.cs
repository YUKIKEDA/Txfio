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
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
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
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
        await StageAsync(PendingChangeKind.Update, path, content, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);
        _locks.AcquireShared(_workFolder);
        if (Directory.Exists(targetPath))
        {
            _locks.AcquireReserving(_workFolder, new[] { targetPath }, targetPath);
        }
        else
        {
            _locks.Acquire(_workFolder, targetPath);
        }

        StagingRules.EnsureParentDirectoryExists(targetPath);

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
            _paths.Rows.Add(directoryDelete);
            try
            {
                await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _paths.Rows.Remove(directoryDelete);
                throw;
            }

            return;
        }

        StagingRules.EnsureTargetMatchesKind(PendingChangeKind.Delete, targetPath);
        JournalOperation operation = new JournalOperation(PendingChangeKind.Delete, targetPath);
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
    }

    /// <inheritdoc />
    public async Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);

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
    public async Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        BeginLockAttempt(cancellationToken);
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

        int destIndex = FindOperationIndex(destPath);
        bool destIsMoveSource = destIndex >= 0 && _paths.Rows[destIndex].Kind == PendingChangeKind.Move;
        if (destIndex >= 0 && !destIsMoveSource)
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

        int sourceIndex = FindOperationIndex(sourcePath);
        int moveToSource = FindMoveToIndex(sourcePath);
        if (IsDirectoryMove(sourcePath, sourceIndex, moveToSource))
        {
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

        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, sourcePath, destPath);
        StagingRules.EnsureParentDirectoryExists(destPath);
        if (!destIsMoveSource)
        {
            StagingRules.EnsureMoveDestinationIsFree(destPath);
        }

        if (sourceIndex >= 0)
        {
            await MovePendingSourceAsync(sourceIndex, destPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (moveToSource >= 0)
        {
            JournalOperation existing = _paths.Rows[moveToSource];
            JournalOperation folded = new JournalOperation(
                PendingChangeKind.Move,
                existing.Path,
                stagingPath: null,
                destPath);
            ThrowIfMoveChainCloses(folded, moveToSource);
            await PersistReplacingOperationAsync(moveToSource, folded, cancellationToken).ConfigureAwait(false);
            return;
        }

        StagingRules.EnsureMoveSourceExists(sourcePath);
        JournalOperation operation = new JournalOperation(
            PendingChangeKind.Move,
            sourcePath,
            stagingPath: null,
            destPath);
        ThrowIfMoveChainCloses(operation, replaceIndex: -1);
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
    }

    private async Task PersistReplacingOperationAsync(
        int existingIndex,
        JournalOperation? replacement,
        CancellationToken cancellationToken)
    {
        JournalOperation existing = _paths.Rows[existingIndex];
        if (replacement is null)
        {
            _paths.Rows.RemoveAt(existingIndex);
        }
        else
        {
            _paths.Rows[existingIndex] = replacement;
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (replacement is null)
            {
                _paths.Rows.Insert(existingIndex, existing);
            }
            else
            {
                _paths.Rows[existingIndex] = existing;
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
        _locks.AcquireShared(_workFolder);
        _locks.AcquireReserving(_workFolder, new[] { root, destPath }, root, destPath);
        using PathLockSet.WorkFolderExclusive exclusive = _locks.EnterExclusive(_workFolder);
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
        _locks.AcquireShared(_workFolder);
        _locks.AcquireReserving(_workFolder, new[] { directoryPath }, directoryPath);
        using PathLockSet.WorkFolderExclusive exclusive = _locks.EnterExclusive(_workFolder);
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
    }

    private async Task MovePendingSourceAsync(
        int sourceIndex,
        string destPath,
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
                destPath);
            ThrowIfMoveChainCloses(folded, sourceIndex);
            await PersistReplacingOperationAsync(sourceIndex, folded, cancellationToken).ConfigureAwait(false);
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
        JournalOperation existing = _paths.Rows[sourceIndex];
        JournalOperation[] previous = _paths.Rows.ToArray();

        // .txnew は移動先の名前に付け替える（元の名前のままだと、移動元へ次に書いたときに同じ .txnew を上書きする）
        string? stagingPath = existing.StagingPath is null
            ? null
            : WorkPath.StagingFilePath(destPath, _transactionId);
        bool journalUpdated = false;
        try
        {
            string sourcePath = existing.Path;
            _paths.Rows.RemoveAt(sourceIndex);
            _paths.Rows.Add(new JournalOperation(PendingChangeKind.Add, destPath, stagingPath));
            if (deleteSource)
            {
                _paths.Rows.Add(new JournalOperation(PendingChangeKind.Delete, sourcePath));
            }

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            journalUpdated = true;
            if (existing.StagingPath is not null)
            {
                File.Move(existing.StagingPath, stagingPath!);
            }
        }
        catch
        {
            _paths.Rows.Clear();
            _paths.Rows.AddRange(previous);
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
        await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);

        JournalOperation[] previous = _paths.Rows.ToArray();
        try
        {
            _paths.Rows.Clear();
            _paths.Rows.AddRange(folded);
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Rows.Clear();
            _paths.Rows.AddRange(previous);
            StagingFile.TryDelete(stagingPath);
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
        JournalOperation[] previous = _paths.Rows.ToArray();
        _paths.Rows.Clear();
        _paths.Rows.AddRange(folded);
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _paths.Rows.Clear();
            _paths.Rows.AddRange(previous);
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
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, targetPath);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, targetPath);
        StagingRules.ThrowIfCreateDirectoryPath(_paths.Rows, targetPath);

        _paths.PlanStageFile(
            targetPath,
            kind,
            out int existingIndex,
            out int moveToIndex,
            out bool addOntoFileMove,
            out PendingChangeKind recordedKind);

        _locks.AcquireShared(_workFolder);
        _locks.Acquire(_workFolder, targetPath);
        StagingRules.EnsureParentDirectoryExists(targetPath);
        if (kind == PendingChangeKind.Update)
        {
            _externalChanges?.NoteUpdate(_paths.Rows, targetPath, _transactionId);
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
        JournalOperation? staged = null;
        bool journalUpdated = false;
        try
        {
            staged = new JournalOperation(recordedKind, targetPath, stagingPath);
            if (existingIndex >= 0)
            {
                _paths.Rows[existingIndex] = staged;
            }
            else
            {
                _paths.Rows.Add(staged);
            }

            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            journalUpdated = true;
            if (previous?.StagingPath is not null
                && string.Equals(previous.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(stagingPath))
            {
                backupPath = WorkPath.StagingBackupPath(stagingPath);
                StagingFile.MoveReplacing(stagingPath, backupPath);
            }

            await StagingFile.WriteAsync(stagingPath, content, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (staged is not null)
            {
                if (existingIndex < 0)
                {
                    _paths.Rows.Remove(staged);
                }
                else
                {
                    _paths.Rows[existingIndex] = previous!;
                }
            }

            if (backupPath is not null)
            {
                try
                {
                    StagingFile.MoveReplacing(backupPath, stagingPath);
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
