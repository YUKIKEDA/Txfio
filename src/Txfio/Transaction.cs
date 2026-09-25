namespace Txfio;

/// <summary>
/// ワークフォルダに対する 1 件のトランザクション
/// </summary>
internal sealed partial class Transaction : ITransaction
{
    private readonly string _workFolder;
    private readonly Guid _transactionId;
    private readonly string _journalPath;
    private readonly List<JournalOperation> _operations = new List<JournalOperation>();
    private readonly List<string> _createdDirectories = new List<string>();
    private readonly PathLockSet _locks = new PathLockSet();
    private FileStream? _liveness;
    private bool _committed;
    private bool _committingWritten;
    private bool _disposed;
    private int _callDepth;

    /// <summary>
    /// 指定したワークフォルダとジャーナルでトランザクションを開始する
    /// </summary>
    /// <param name="workFolder">対象のワークフォルダ</param>
    /// <param name="transactionId">このトランザクションの ID</param>
    /// <param name="journalPath">このトランザクションのジャーナルファイル</param>
    /// <param name="liveness">トランザクションが終わるまで持つ生存ロック</param>
    internal Transaction(string workFolder, Guid transactionId, string journalPath, FileStream liveness)
    {
        _workFolder = workFolder;
        _transactionId = transactionId;
        _journalPath = journalPath;
        _liveness = liveness;
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        using CallScope scope = EnterCall();
        ObjectDisposedException.ThrowIf(_disposed, this);
        PendingChange[] result = new PendingChange[_operations.Count];
        for (int i = 0; i < _operations.Count; i++)
        {
            JournalOperation operation = _operations[i];
            result[i] = new PendingChange(operation.Kind, operation.Path, operation.NewPath);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using CallScope scope = EnterCall();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_committed)
        {
            ReleaseLiveness();
            return;
        }

        // Committing を書いたあとはロールバックしない。ジャーナルを残し、次の Recover が進める
        if (CrashInjector.ShouldSkipRollback || _committingWritten)
        {
            _locks.Release();
            ReleaseLiveness();
            return;
        }

        try
        {
            foreach (JournalOperation operation in _operations)
            {
                StagingFile.TryDelete(operation.StagingPath);
            }

            StagingApplier.DeleteCreateDirectoryTrees(_operations);
            StagingApplier.DeleteStagingBackups(_workFolder, _transactionId);
            DeleteCreatedDirectoriesFrom(0, ignoreIoFailures: false);
            await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
        }
        finally
        {
            _locks.Release();
            ReleaseLiveness();
        }
    }

    private CallScope EnterCall()
    {
        if (Interlocked.Increment(ref _callDepth) != 1)
        {
            Interlocked.Decrement(ref _callDepth);

            // 入れなかった呼び出しは数えず、先に入った呼び出しを続ける
            throw new InvalidOperationException("同じトランザクションへの呼び出しが重なっています");
        }

        return new CallScope(this);
    }

    private void ExitCall()
    {
        Interlocked.Decrement(ref _callDepth);
    }

    private void ThrowIfCannotMutate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("このトランザクションは既にコミット済みです");
        }
    }

    private int FindOperationIndex(string path)
    {
        for (int i = 0; i < _operations.Count; i++)
        {
            if (string.Equals(_operations[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindLaterOperationIndex(string path, int afterIndex)
    {
        for (int i = afterIndex + 1; i < _operations.Count; i++)
        {
            if (string.Equals(_operations[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsFileMoveOut(int index)
    {
        return index >= 0
            && _operations[index].Kind == PendingChangeKind.Move
            && !_operations[index].IsDirectory;
    }

    // ファイル Move の移動元のパスで、そのあとの中身を決める操作。移動元へ書き直した操作か、別の Move で入ってくる操作
    private int FindContentAfterMoveOut(string path, int moveOutIndex)
    {
        int later = FindLaterOperationIndex(path, moveOutIndex);
        return later >= 0 ? later : FindMoveToIndex(path);
    }

    private void ThrowIfMoveChainCloses(JournalOperation replacement, int replaceIndex)
    {
        List<JournalOperation> prospective = new List<JournalOperation>(_operations);
        if (replaceIndex >= 0)
        {
            prospective[replaceIndex] = replacement;
        }
        else
        {
            prospective.Add(replacement);
        }

        if (!StagingApplier.MovesReachFreeEnd(prospective))
        {
            throw new InvalidOperationException("空いている端が無い移動は受け付けられません");
        }
    }

    private int FindMoveToIndex(string destPath)
    {
        for (int i = 0; i < _operations.Count; i++)
        {
            JournalOperation operation = _operations[i];
            if (operation.Kind == PendingChangeKind.Move
                && string.Equals(operation.NewPath, destPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // ジャーナルを消したあとで呼ぶ。先に閉じると、Recover が生きているトランザクションを巻き戻しうる
    private void ReleaseLiveness()
    {
        _liveness?.Dispose();
        _liveness = null;
    }

    private Task PersistAsync(bool committing, CancellationToken cancellationToken)
    {
        JournalDocument document = new JournalDocument(
            JournalStore.CurrentVersion,
            _transactionId,
            committing,
            _operations.ToArray(),
            _createdDirectories.ToArray());
        return JournalStore.SaveAsync(_journalPath, document, cancellationToken);
    }

    private async Task TryPersistUndoAsync()
    {
        try
        {
            await PersistAsync(committing: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // ジャーナルが残っていれば、次の Recover が消す
        }
        catch (UnauthorizedAccessException)
        {
            // ジャーナルが残っていれば、次の Recover が消す
        }
    }

    /// <summary>
    /// 公開呼び出しの監視をメソッド終了時に閉じる
    /// </summary>
    private readonly struct CallScope : IDisposable
    {
        private readonly Transaction _transaction;

        /// <summary>
        /// 重なりの監視を始める
        /// </summary>
        /// <param name="transaction">対象のトランザクション</param>
        public CallScope(Transaction transaction)
        {
            _transaction = transaction;
        }

        /// <summary>
        /// 公開呼び出しを 1 つ終える
        /// </summary>
        public void Dispose()
        {
            _transaction.ExitCall();
        }
    }
}
