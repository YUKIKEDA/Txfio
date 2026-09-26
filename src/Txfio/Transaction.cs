namespace Txfio;

/// <summary>
/// ワークフォルダに対する 1 件のトランザクション
/// </summary>
internal sealed partial class Transaction : ITransaction
{
    private readonly string _workFolder;
    private readonly Guid _transactionId;
    private readonly string _journalPath;
    private readonly PathTable _paths = new PathTable();
    private readonly List<string> _createdDirectories = new List<string>();
    private readonly IFaultInjector _faults;
    private readonly PathLockSet _locks;
    private readonly TimeSpan _lockWait;
    private readonly ExternalChangeSet? _externalChanges;
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
    /// <param name="lockWait">ロックが取れないとき、公開メソッド 1 回ごとに待つ上限</param>
    /// <param name="detectExternalChanges"><see langword="true"/> のとき、ステージ後に記録と違う Update をコミット前に失敗にする</param>
    /// <param name="faults">このトランザクションの失敗と途中停止</param>
    internal Transaction(
        string workFolder,
        Guid transactionId,
        string journalPath,
        FileStream liveness,
        TimeSpan lockWait,
        bool detectExternalChanges,
        IFaultInjector faults)
    {
        _workFolder = workFolder;
        _transactionId = transactionId;
        _journalPath = journalPath;
        _liveness = liveness;
        _lockWait = lockWait;
        _faults = faults;
        _locks = new PathLockSet(faults);
        _externalChanges = detectExternalChanges ? new ExternalChangeSet() : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        using CallScope scope = EnterCall();
        ObjectDisposedException.ThrowIf(_disposed, this);
        PendingChange[] result = new PendingChange[_paths.Count];
        for (int i = 0; i < _paths.Count; i++)
        {
            JournalOperation operation = _paths[i];
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

        // Committing を書いたあとはロールバックしない（ジャーナルを残し、次の Recover が進める）
        if (_faults.ShouldSkipRollback || _committingWritten)
        {
            _locks.Release();
            ReleaseLiveness();
            return;
        }

        try
        {
            bool cleanupSucceeded = true;
            foreach (JournalOperation operation in _paths.Rows)
            {
                if (!TryCleanup(() => OperationKind.DeleteStaging(operation)))
                {
                    cleanupSucceeded = false;
                }
            }

            if (!StagingApplier.DeleteCreateDirectoryTrees(_paths.Rows, ignoreIoFailures: true))
            {
                cleanupSucceeded = false;
            }

            if (!StagingApplier.DeleteStagingBackups(_workFolder, _transactionId, ignoreIoFailures: true))
            {
                cleanupSucceeded = false;
            }

            if (!DeleteCreatedDirectoriesFrom(0, ignoreIoFailures: true))
            {
                cleanupSucceeded = false;
            }

            if (cleanupSucceeded)
            {
                try
                {
                    await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // ジャーナルは残し、Dispose からは投げない
                }
            }
        }
        finally
        {
            _locks.Release();
            ReleaseLiveness();
        }
    }

    private static bool TryCleanup(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void BeginLockAttempt(CancellationToken cancellationToken)
    {
        _locks.BeginAttempt(_lockWait, cancellationToken);
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
        return _paths.FindOperationIndex(path);
    }

    private int FindLaterOperationIndex(string path, int afterIndex)
    {
        return _paths.FindLaterOperationIndex(path, afterIndex);
    }

    private bool IsFileMoveOut(int index)
    {
        return _paths.IsFileMoveOut(index);
    }

    // ファイル Move の移動元のパスで、そのあとの中身を決める操作（移動元へ書き直した操作か、別の Move で入ってくる操作）
    private int FindContentAfterMoveOut(string path, int moveOutIndex)
    {
        return _paths.FindContentAfterMoveOut(path, moveOutIndex);
    }

    private void ThrowIfMoveChainCloses(JournalOperation replacement, int replaceIndex)
    {
        _paths.ThrowIfMoveChainCloses(replacement, replaceIndex);
    }

    private int FindMoveToIndex(string destPath)
    {
        return _paths.FindMoveToIndex(destPath);
    }

    // ジャーナルを消したあとで呼ぶ（先に閉じると、Recover が生きているトランザクションを巻き戻しうる）
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
            _paths.ToArray(),
            _createdDirectories.ToArray());
        return JournalStore.SaveAsync(_journalPath, document, cancellationToken);
    }

    /// <summary>
    /// 表を変えてジャーナルを先に書き、そのあと実体を作る（途中で失敗したら、作りかけの実体を消し、表を戻し、書いたジャーナルも戻してから例外を返す）
    /// </summary>
    /// <remarks>
    /// ステージのすべての変更はここを通す（ジャーナルが先、実体が後の順を 1 か所で守る）
    /// </remarks>
    /// <param name="record">表を変える</param>
    /// <param name="materialize">ジャーナルを書いたあとで実体を作る（無ければ null）</param>
    /// <param name="discard">失敗したとき作りかけの実体を消す（無ければ null。<see cref="IOException"/> と <see cref="UnauthorizedAccessException"/> は元の例外を優先して握る）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ジャーナルと実体の完了</returns>
    private async Task RecordThenMaterializeAsync(
        Action record,
        Func<Task>? materialize,
        Action? discard,
        CancellationToken cancellationToken)
    {
        JournalOperation[] previous = _paths.ToArray();
        bool journaled = false;
        record();
        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            journaled = true;
            if (materialize is not null)
            {
                await materialize().ConfigureAwait(false);
            }
        }
        catch
        {
            if (discard is not null)
            {
                TryCleanup(discard);
            }

            _paths.Load(previous);
            if (journaled)
            {
                await TryPersistUndoAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 表を変えてジャーナルを書く（書けなければ表を戻して例外を返す）
    /// </summary>
    /// <param name="record">表を変える</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ジャーナルの書き込みの完了</returns>
    private Task RecordAsync(Action record, CancellationToken cancellationToken)
    {
        return RecordThenMaterializeAsync(record, materialize: null, discard: null, cancellationToken);
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
