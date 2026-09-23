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
    private readonly PathLockSet _locks = new PathLockSet();
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// 指定したワークフォルダとジャーナルでトランザクションを開始する
    /// </summary>
    /// <param name="workFolder">対象のワークフォルダ</param>
    /// <param name="transactionId">このトランザクションの ID</param>
    /// <param name="journalPath">このトランザクションのジャーナルファイル</param>
    internal Transaction(string workFolder, Guid transactionId, string journalPath)
    {
        _workFolder = workFolder;
        _transactionId = transactionId;
        _journalPath = journalPath;
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (CrashInjector.ShouldSkipRollback)
        {
            _locks.Release();
            return;
        }

        if (_committed)
        {
            return;
        }

        foreach (JournalOperation operation in _operations)
        {
            StagingFile.TryDelete(operation.StagingPath);
        }

        await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
        _locks.Release();
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

    private Task PersistAsync(bool committing, CancellationToken cancellationToken)
    {
        JournalDocument document = new JournalDocument(
            JournalStore.CurrentVersion,
            _transactionId,
            committing,
            _operations.ToArray());
        return JournalStore.SaveAsync(_journalPath, document, cancellationToken);
    }
}
