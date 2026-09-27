namespace Txfio;

/// <summary>
/// One transaction on a work folder.
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

    // The rows and created directories known to be in the journal (set to null when unknown, to rewrite the whole journal).
    private JournalOperation[]? _persistedRows = Array.Empty<JournalOperation>();
    private string[]? _persistedDirectories = Array.Empty<string>();
    private FileStream? _liveness;
    private bool _committed;
    private bool _committingWritten;
    private bool _disposed;
    private int _callDepth;
    private LockAttempt _lockAttempt;

    /// <summary>
    /// Initializes a new instance of the <see cref="Transaction"/> class on the given work folder and journal.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="transactionId">The ID of this transaction.</param>
    /// <param name="journalPath">The journal file of this transaction.</param>
    /// <param name="liveness">The liveness lock held until the transaction ends.</param>
    /// <param name="lockWait">How long each public method call waits when a lock cannot be taken.</param>
    /// <param name="detectExternalChanges">When <see langword="true"/>, an Update whose file differs from the record after staging fails before commit.</param>
    /// <param name="faults">The failures and partial stops of this transaction.</param>
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
        await CallerContext.LeaveAsync();
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

        // After Committing is written, do not roll back (keep the journal; the next Recover rolls forward).
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

            if (!StagingApplier.DeleteStagingBackups(_paths.Rows, ignoreIoFailures: true))
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
                    // Keep the journal, and do not throw from Dispose.
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

    private static bool StartsWith<T>(T[] items, T[] prefix)
        where T : class
    {
        if (items.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (!ReferenceEquals(items[i], prefix[i]) && !Equals(items[i], prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    // The lock wait deadline counts from entering a public call, and returns to the no-wait default when the call exits.
    private CallScope EnterCall(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _callDepth) != 1)
        {
            Interlocked.Decrement(ref _callDepth);

            // A call that could not enter is not counted, and the call that entered first continues.
            throw new InvalidOperationException("Calls to the same transaction overlap");
        }

        _lockAttempt = LockAttempt.Start(_lockWait, cancellationToken);
        return new CallScope(this);
    }

    private void ExitCall()
    {
        _lockAttempt = default;
        Interlocked.Decrement(ref _callDepth);
    }

    private void ThrowIfCannotMutate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("This transaction has already been committed");
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

    // For the source path of a file Move, the operation that decides the later content (a write back to the source, or a Move coming in from elsewhere).
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

    // Call this after the journal is deleted (closing it first could let Recover roll back a live transaction).
    private void ReleaseLiveness()
    {
        _liveness?.Dispose();
        _liveness = null;
    }

    // If rows were only added to the end of the table, append one line; otherwise (the middle changed, or Committing), rewrite the whole journal.
    private async Task PersistAsync(bool committing, CancellationToken cancellationToken)
    {
        JournalOperation[] rows = _paths.ToArray();
        string[] directories = _createdDirectories.ToArray();
        JournalOperation[]? persistedRows = _persistedRows;
        string[]? persistedDirectories = _persistedDirectories;
        if (!committing
            && persistedRows is not null
            && persistedDirectories is not null
            && StartsWith(rows, persistedRows)
            && StartsWith(directories, persistedDirectories))
        {
            if (rows.Length == persistedRows.Length && directories.Length == persistedDirectories.Length)
            {
                return;
            }

            // If an append fails partway, a half-written line may remain, so rewrite the whole journal next time.
            _persistedRows = null;
            _persistedDirectories = null;
            await JournalStore.AppendAsync(
                    _journalPath,
                    rows.AsSpan(persistedRows.Length).ToArray(),
                    directories.AsSpan(persistedDirectories.Length).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            _persistedRows = rows;
            _persistedDirectories = directories;
            return;
        }

        JournalDocument document = new JournalDocument(
            JournalStore.CurrentVersion,
            _transactionId,
            committing,
            rows,
            directories);
        _persistedRows = null;
        _persistedDirectories = null;
        await JournalStore.SaveAsync(_journalPath, document, cancellationToken).ConfigureAwait(false);
        _persistedRows = rows;
        _persistedDirectories = directories;
    }

    /// <summary>
    /// Changes the table, writes the journal first, then creates things on disk (on failure partway, deletes what was partly created, restores the table, restores the journal, then throws).
    /// </summary>
    /// <remarks>
    /// Changes that create things on disk after changing the table go through here (journal first, disk after; moving staged content, folding an Update at a Move destination, and creating a ZIP are separate because they undo differently after a crash).
    /// </remarks>
    /// <param name="record">Changes the table.</param>
    /// <param name="materialize">Creates things on disk after the journal is written (<see langword="null"/> if none).</param>
    /// <param name="discard">Deletes what was partly created on failure (<see langword="null"/> if none; does not propagate <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the journal and the disk changes are done.</returns>
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
    /// Changes the table and writes the journal (if it cannot be written, restores the table and throws).
    /// </summary>
    /// <param name="record">Changes the table.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the journal is written.</returns>
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
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            // If the journal remains, the next Recover deletes them.
        }
    }

    /// <summary>
    /// Closes the watch on a public call when the method ends.
    /// </summary>
    private readonly struct CallScope : IDisposable
    {
        private readonly Transaction _transaction;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallScope"/> struct and starts watching for overlaps.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        public CallScope(Transaction transaction)
        {
            _transaction = transaction;
        }

        /// <summary>
        /// Ends one public call.
        /// </summary>
        public void Dispose()
        {
            _transaction.ExitCall();
        }
    }
}
