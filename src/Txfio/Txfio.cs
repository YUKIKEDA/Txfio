namespace Txfio;

/// <summary>
/// The entry point of transactional file IO.
/// </summary>
public static class Txfio
{
    /// <summary>
    /// Begins a transaction on a work folder (with no lock wait).
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    public static Task<ITransaction> BeginAsync(string path, CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, TimeSpan.Zero, detectExternalChanges: false, cancellationToken);
    }

    /// <summary>
    /// Begins a transaction on a work folder (with no lock wait).
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="detectExternalChanges">When <see langword="true"/>, an Update whose file differs from the record after staging fails before commit.</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    public static Task<ITransaction> BeginAsync(
        string path,
        bool detectExternalChanges,
        CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, TimeSpan.Zero, detectExternalChanges, cancellationToken);
    }

    /// <summary>
    /// Begins a transaction on a work folder.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="lockWait">How long each public method call waits when a lock cannot be taken (zero does not wait).</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    public static Task<ITransaction> BeginAsync(string path, TimeSpan lockWait, CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, lockWait, detectExternalChanges: false, cancellationToken);
    }

    /// <summary>
    /// Begins a transaction on a work folder.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="lockWait">How long each public method call waits when a lock cannot be taken (zero does not wait).</param>
    /// <param name="detectExternalChanges">When <see langword="true"/>, an Update whose file differs from the record after staging fails before commit.</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    public static Task<ITransaction> BeginAsync(
        string path,
        TimeSpan lockWait,
        bool detectExternalChanges,
        CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, lockWait, detectExternalChanges, NoFaultInjector.Instance, cancellationToken);
    }

    /// <summary>
    /// Detects unfinished transactions and rolls them back or forward (with no wait for the work-folder lock).
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="cancellationToken">The token to cancel detection and recovery.</param>
    /// <returns>The overall result and the journals processed (in lexical order ignoring case; <see cref="RecoverResult.JournalUnreadable"/> if a journal cannot be read as JSON).</returns>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the work folder.</exception>
    /// <exception cref="IOException">Reading a journal failed (that journal stays).</exception>
    public static Task<RecoverReport> RecoverAsync(string path, CancellationToken cancellationToken = default)
    {
        return RecoverAsync(path, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Detects unfinished transactions and rolls them back or forward.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="lockWait">How long this call waits when the work-folder lock cannot be taken (zero does not wait).</param>
    /// <param name="cancellationToken">The token to cancel detection and recovery.</param>
    /// <returns>The overall result and the journals processed (in lexical order ignoring case; <see cref="RecoverResult.JournalUnreadable"/> if a journal cannot be read as JSON).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="LockContentionException">The work folder cannot be taken by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait for the work-folder lock was canceled.</exception>
    /// <exception cref="IOException">Reading a journal failed (that journal stays).</exception>
    public static async Task<RecoverReport> RecoverAsync(string path, TimeSpan lockWait, CancellationToken cancellationToken = default)
    {
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await CallerContext.LeaveAsync();
        string workFolder = NormalizeWorkFolder(path);

        return await RecoverService.RecoverAsync(workFolder, lockWait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Begins a transaction on a work folder (with no lock wait). <paramref name="faults"/> handles failures and partial stops.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="faults">The failures and partial stops of this transaction.</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    internal static Task<ITransaction> BeginAsync(
        string path,
        IFaultInjector faults,
        CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, TimeSpan.Zero, detectExternalChanges: false, faults, cancellationToken);
    }

    /// <summary>
    /// Begins a transaction on a work folder. <paramref name="faults"/> handles failures and partial stops.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="lockWait">How long each public method call waits when a lock cannot be taken (zero does not wait).</param>
    /// <param name="detectExternalChanges">When <see langword="true"/>, an Update whose file differs from the record after staging fails before commit.</param>
    /// <param name="faults">The failures and partial stops of this transaction.</param>
    /// <param name="cancellationToken">The token to cancel beginning the transaction.</param>
    /// <returns>The transaction that was started.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains.</exception>
    internal static async Task<ITransaction> BeginAsync(
        string path,
        TimeSpan lockWait,
        bool detectExternalChanges,
        IFaultInjector faults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await CallerContext.LeaveAsync();
        string workFolder = NormalizeWorkFolder(path);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureMetadataFolder(workFolder);
        StaleJournals.ThrowIfAny(workFolder);

        Guid transactionId = Guid.NewGuid();
        string journalPath = MetadataNames.JournalPath(workFolder, transactionId);

        // Open it first so that Recover always gets a sharing violation when it finds the journal of a live transaction.
        FileStream liveness = LivenessLock.Create(MetadataNames.LivenessLockPath(journalPath));
        try
        {
            await JournalStore.WriteNewAsync(journalPath, transactionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await liveness.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Transaction(workFolder, transactionId, journalPath, liveness, lockWait, detectExternalChanges, faults);
    }

    private static string NormalizeWorkFolder(string path)
    {
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new ExternalConflictException("The work folder does not exist: " + workFolder, workFolder);
        }

        return WorkPath.ToLongPath(workFolder);
    }

    private static void EnsureMetadataFolder(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        DirectoryInfo directory = Directory.CreateDirectory(metadataFolder);
        directory.Attributes |= FileAttributes.Hidden;
    }
}
