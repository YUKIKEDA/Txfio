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
    public static Task<ITransaction> BeginAsync(
        string path,
        TimeSpan lockWait,
        bool detectExternalChanges,
        CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, lockWait, detectExternalChanges, NoFaultInjector.Instance, cancellationToken);
    }

    /// <summary>
    /// Detects unfinished transactions and rolls them back or forward, each while holding the locks on its paths (without waiting for locks). Other transactions may stay alive meanwhile.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="cancellationToken">The token to cancel detection and recovery.</param>
    /// <returns>The overall result and the journals processed or left (in lexical order ignoring case; <see cref="RecoverResult.JournalUnreadable"/> if a journal cannot be read as JSON, <see cref="RecoverResult.Contended"/> if another transaction holds the paths of a journal).</returns>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="LockContentionException">The work-folder lock is held exclusively (by the lock-file cleanup of another call).</exception>
    /// <exception cref="IOException">Reading a journal failed (that journal stays).</exception>
    public static Task<RecoverReport> RecoverAsync(string path, CancellationToken cancellationToken = default)
    {
        return RecoverAsync(path, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Detects unfinished transactions and rolls them back or forward, each while holding the locks on its paths. Other transactions may stay alive meanwhile.
    /// </summary>
    /// <param name="path">An existing work folder.</param>
    /// <param name="lockWait">How long this call waits for the work-folder lock, and retries journals whose paths another transaction holds (zero does not wait).</param>
    /// <param name="cancellationToken">The token to cancel detection and recovery.</param>
    /// <returns>The overall result and the journals processed or left (in lexical order ignoring case; <see cref="RecoverResult.JournalUnreadable"/> if a journal cannot be read as JSON, <see cref="RecoverResult.Contended"/> if another transaction holds the paths of a journal).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ExternalConflictException">The work folder does not exist.</exception>
    /// <exception cref="IOException">The long name of the work folder cannot be obtained.</exception>
    /// <exception cref="LockContentionException">The work-folder lock cannot be taken as shared by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait for the work-folder lock was canceled (cancelling while retrying journals returns the report instead).</exception>
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
    /// Returns whether a single file or directory name is the name of a staging entry (<c>{name}.{txid}.txnew</c>, <c>{name}.{txid}.txnew.prev</c>, or <c>{name}.{txid}.txold</c>) of any transaction.
    /// </summary>
    /// <remarks>
    /// <para>Trailing dots and spaces are trimmed before matching, as Windows does when it opens a name. This applies on every OS, so on Linux a name such as <c>a.{txid}.txnew.</c> is a staging name too. The transaction ID is matched ignoring case.</para>
    /// <para>It does not touch the disk. To skip staging entries in a plain enumeration of the work folder, test the last segment of each entry.</para>
    /// <para>A path inside the work folder with a segment that is a staging name is not a target of any API (<see cref="InvalidOperationException"/>).</para>
    /// </remarks>
    /// <param name="name">One file or directory name (not a path).</param>
    /// <returns><see langword="true"/> if the name is a staging name; <see langword="false"/> otherwise, including for <c>""</c>, <c>"."</c>, and <c>".."</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> contains a directory separator (<c>\</c> or <c>/</c>).</exception>
    public static bool IsStagingName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.IndexOfAny(new[] { '\\', '/' }) >= 0)
        {
            throw new ArgumentException("The name must be a single name without a directory separator", nameof(name));
        }

        return StagingNames.IsMatch(name);
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
