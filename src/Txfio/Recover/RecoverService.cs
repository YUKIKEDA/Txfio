namespace Txfio;

/// <summary>
/// Rolls back and rolls forward unfinished journals.
/// </summary>
internal static class RecoverService
{
    /// <summary>
    /// Processes the orphaned journals in the work folder, each while holding the locks of its footprint.
    /// </summary>
    /// <remarks>
    /// The work-folder lock is held as shared, so this runs while other transactions are alive.
    /// A journal whose footprint locks are held by a live transaction is retried until the deadline, and reported as <see cref="RecoverResult.Contended"/> if it still cannot be processed.
    /// </remarks>
    /// <param name="workFolder">An existing work folder.</param>
    /// <param name="lockWait">How long this call waits for the work-folder lock and for the footprint locks.</param>
    /// <param name="cancellationToken">The token to cancel the waits.</param>
    /// <returns>The overall result and the journals processed or left (in lexical order of the path, ignoring case).</returns>
    /// <exception cref="IOException">Reading a journal failed (that journal stays).</exception>
    /// <exception cref="LockContentionException">The work-folder lock cannot be taken by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait for the work-folder lock was canceled (cancelling between rounds returns the report instead).</exception>
    internal static async Task<RecoverReport> RecoverAsync(
        string workFolder,
        TimeSpan lockWait,
        CancellationToken cancellationToken)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return new RecoverReport(RecoverResult.NoPendingTransactions, Array.Empty<JournalReport>());
        }

        LockAttempt attempt = LockAttempt.Start(lockWait, cancellationToken);
        PathLockSet workFolderLock = new PathLockSet();
        bool cancelled;
        RecoverTally tally = new RecoverTally();
        try
        {
            await workFolderLock.AcquireSharedAsync(workFolder, attempt).ConfigureAwait(false);
            string[] journals = Directory.GetFiles(
                metadataFolder,
                MetadataNames.JournalSearchPattern,
                SearchOption.TopDirectoryOnly);
            Array.Sort(journals, static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            List<PendingJournal> pending = new List<PendingJournal>();
            foreach (string journalPath in journals)
            {
                PendingJournal? retry = await FirstAttemptAsync(workFolder, journalPath, tally).ConfigureAwait(false);
                if (retry is not null)
                {
                    pending.Add(retry);
                }
            }

            cancelled = await RetryInRoundsAsync(workFolder, pending, attempt, tally).ConfigureAwait(false);
            foreach (PendingJournal left in pending)
            {
                // Another RecoverAsync may have finished it meanwhile.
                if (File.Exists(left.JournalPath))
                {
                    tally.Add(left.JournalPath, new JournalReport(left.Document.TransactionId, RecoverResult.Contended, Array.Empty<OperationReport>()));
                }
            }

            DeleteOrphanJournalTemps(metadataFolder);
        }
        finally
        {
            workFolderLock.Release();
        }

        if (!cancelled)
        {
            await TryDeleteLockFilesAsync(workFolder).ConfigureAwait(false);
        }

        return tally.ToReport();
    }

    // Returns the journal to retry when its footprint locks or its liveness lock could not be taken.
    private static async Task<PendingJournal?> FirstAttemptAsync(string workFolder, string journalPath, RecoverTally tally)
    {
        // Open and close right away: a transaction's orphan check takes a held liveness lock as a live owner.
        using (FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath)))
        {
            if (liveness is null)
            {
                return null;
            }
        }

        JournalReadResult? read = await JournalStore.ReadOrphanAsync(journalPath, CancellationToken.None).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        if (read.Document is null)
        {
            RecoverUnreadable(workFolder, journalPath, read, tally);
            return null;
        }

        PendingJournal journal = new PendingJournal(journalPath, read.Document);
        return await TryProcessAsync(workFolder, journal, tally).ConfigureAwait(false) ? null : journal;
    }

    // Returns true when the attempt is over (cancelled between rounds); the remaining journals stay in the list.
    private static async Task<bool> RetryInRoundsAsync(
        string workFolder,
        List<PendingJournal> pending,
        LockAttempt attempt,
        RecoverTally tally)
    {
        while (pending.Count > 0)
        {
            try
            {
                if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                // The first pass may already have processed journals; throwing would lose their results.
                return true;
            }

            for (int i = 0; i < pending.Count;)
            {
                PendingJournal journal = pending[i];
                if (!File.Exists(journal.JournalPath))
                {
                    pending.RemoveAt(i);
                    continue;
                }

                if (await TryProcessAsync(workFolder, journal, tally).ConfigureAwait(false))
                {
                    pending.RemoveAt(i);
                    continue;
                }

                i++;
            }
        }

        return false;
    }

    // Tries the footprint locks and the liveness lock once each; returns false to retry later.
    private static async Task<bool> TryProcessAsync(string workFolder, PendingJournal journal, RecoverTally tally)
    {
        OrphanFootprint footprint = OrphanFootprint.From(journal.Document);
        string[] entries = footprint.Entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        PathLockSet footprintLocks = new PathLockSet();
        try
        {
            try
            {
                await footprintLocks.AcquireReservingAsync(
                        workFolder,
                        entries,
                        entries,
                        LockAttempt.Start(TimeSpan.Zero, CancellationToken.None))
                    .ConfigureAwait(false);
            }
            catch (LockContentionException)
            {
                return false;
            }

            FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journal.JournalPath));
            if (liveness is null)
            {
                // Another opener holds it for a moment; try again in the next round.
                return false;
            }

            await using (liveness.ConfigureAwait(false))
            {
                if (!File.Exists(journal.JournalPath))
                {
                    // Another RecoverAsync finished it.
                    return true;
                }

                // Delete the temporary file of a crashed overwrite.
                JournalStore.DeleteTemp(journal.JournalPath);
                tally.Add(journal.JournalPath, await ProcessAsync(journal).ConfigureAwait(false));
                return true;
            }
        }
        finally
        {
            footprintLocks.Release();
        }
    }

    private static async Task<JournalReport> ProcessAsync(PendingJournal journal)
    {
        JournalDocument document = journal.Document;
        if (document.Committing)
        {
            bool appliedAll = StagingApplier.TryApplyAll(
                document.Operations,
                NoFaultInjector.Instance,
                out OperationReport[] skipped);
            if (!appliedAll)
            {
                // Report the result only once, and do not redo it in the next Recover.
                StagingApplier.DeleteStagingFiles(document.Operations);
                await JournalStore.DeleteAsync(journal.JournalPath).ConfigureAwait(false);
                return new JournalReport(document.TransactionId, RecoverResult.ConflictDetected, skipped);
            }

            await JournalStore.DeleteAsync(journal.JournalPath).ConfigureAwait(false);
            return new JournalReport(document.TransactionId, RecoverResult.RolledForward, Array.Empty<OperationReport>());
        }

        StagingApplier.DeleteCreateDirectoryTrees(document.Operations);
        StagingApplier.DeleteStagingFiles(document.Operations);
        StagingApplier.DeleteStagingBackups(document.Operations);
        StagingApplier.DeleteCreatedDirectories(document.CreatedDirectories);
        await JournalStore.DeleteAsync(journal.JournalPath).ConfigureAwait(false);
        return new JournalReport(document.TransactionId, RecoverResult.RolledBack, Array.Empty<OperationReport>());
    }

    // The document cannot be read, so no footprint locks are taken; the files deleted carry this journal's ID, so no other transaction's work is touched.
    private static void RecoverUnreadable(string workFolder, string journalPath, JournalReadResult read, RecoverTally tally)
    {
        tally.MarkUnreadable();
        if (!MetadataNames.TryGetTransactionId(journalPath, out Guid transactionId))
        {
            return;
        }

        // With a different version, it may have been left by a newer library, so nothing is touched.
        if (!read.UnsupportedVersion)
        {
            JournalStore.DeleteTemp(journalPath);
            StagingApplier.DeleteStagingFiles(workFolder, transactionId);
        }

        tally.Add(journalPath, new JournalReport(transactionId, RecoverResult.JournalUnreadable, Array.Empty<OperationReport>()));
    }

    // Optional last step: only when the exclusive work-folder lock and the share-lost marker are free right now. No failure here is thrown, because every journal has already been processed.
    private static async Task TryDeleteLockFilesAsync(string workFolder)
    {
        PathLockSet exclusive = new PathLockSet();
        try
        {
            LockAttempt once = LockAttempt.Start(TimeSpan.Zero, CancellationToken.None);
            await exclusive.AcquireExclusiveAsync(workFolder, once).ConfigureAwait(false);
            await exclusive.RejectForeignLocksAsync(workFolder, once).ConfigureAwait(false);
            DeleteLockFiles(workFolder);
        }
        catch (LockContentionException)
        {
            // In use: leave the lock files for a later RecoverAsync.
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            // Lock files that stay only use disk space.
        }
        finally
        {
            exclusive.Release();
        }
    }

    // While the whole work folder is held exclusively, no other transaction holds a path lock or intent lock (holding one needs the shared work-folder lock or the share-lost marker).
    private static void DeleteLockFiles(string workFolder)
    {
        string lockFolder = MetadataNames.LockFolderPath(workFolder);
        if (!Directory.Exists(lockFolder))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(lockFolder, "*.lock", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leave what cannot be deleted (the next Recover tries again).
            }
        }
    }

    // A crash before the first journal is renamed leaves only the temporary file (do not touch it if the owner is alive).
    private static void DeleteOrphanJournalTemps(string metadataFolder)
    {
        string[] temps = Directory.GetFiles(
            metadataFolder,
            MetadataNames.JournalTempSearchPattern,
            SearchOption.TopDirectoryOnly);
        foreach (string tempPath in temps)
        {
            if (!MetadataNames.TryGetJournalPathFromTemp(tempPath, out string journalPath)
                || File.Exists(journalPath))
            {
                continue;
            }

            using FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is not null && !File.Exists(journalPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
