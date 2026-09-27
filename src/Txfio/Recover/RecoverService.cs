namespace Txfio;

/// <summary>
/// Rolls back and rolls forward unfinished journals.
/// </summary>
internal static class RecoverService
{
    /// <summary>
    /// Processes the orphaned journals in the work folder in lexical order of the path, ignoring case.
    /// </summary>
    /// <param name="workFolder">An existing work folder.</param>
    /// <param name="lockWait">How long this call waits when the work-folder lock cannot be taken.</param>
    /// <param name="cancellationToken">The token to cancel detection and recovery.</param>
    /// <returns>The overall result and the journals processed (in lexical order ignoring case; <see cref="RecoverResult.JournalUnreadable"/> if a journal cannot be read as JSON).</returns>
    /// <exception cref="IOException">Reading a journal failed (that journal stays).</exception>
    /// <exception cref="LockContentionException">The work-folder lock cannot be taken by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait for the work-folder lock was canceled.</exception>
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

        // Do not delete, by roll-forward or rollback, data that another transaction committed during processing.
        PathLockSet sentinel = new PathLockSet();
        try
        {
            LockAttempt attempt = LockAttempt.Start(lockWait, cancellationToken);
            await sentinel.AcquireExclusiveAsync(workFolder, attempt).ConfigureAwait(false);
            await sentinel.RejectForeignLocksAsync(workFolder, attempt).ConfigureAwait(false);
            DeleteLockFiles(workFolder);
            string[] journals = Directory.GetFiles(
                metadataFolder,
                MetadataNames.JournalSearchPattern,
                SearchOption.TopDirectoryOnly);

            // Lexical order of the path, ignoring case (so the report, and what is finished before a read failure, are the same every time).
            Array.Sort(journals, static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            RecoverReport report = await RecoverJournalsAsync(workFolder, journals, cancellationToken).ConfigureAwait(false);
            DeleteOrphanJournalTemps(metadataFolder);
            return report;
        }
        finally
        {
            sentinel.Release();
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

    private static async Task<RecoverReport> RecoverJournalsAsync(
        string workFolder,
        string[] journals,
        CancellationToken cancellationToken)
    {
        bool rolledBack = false;
        bool rolledForward = false;
        bool conflictDetected = false;
        bool journalUnreadable = false;
        List<JournalReport> reports = new List<JournalReport>();
        foreach (string journalPath in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If it cannot be opened, the owner is alive, so neither the journal nor its leftovers are touched.
            FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is null)
            {
                continue;
            }

            try
            {
                // If the owner finished normally after the listing, do nothing.
                if (!File.Exists(journalPath))
                {
                    continue;
                }

                // Delete the temporary file of a crashed overwrite before reading.
                JournalStore.DeleteTemp(journalPath);
                JournalReadResult read = await JournalStore.ReadAsync(journalPath, cancellationToken)
                    .ConfigureAwait(false);
                JournalDocument? document = read.Document;
                if (document is null)
                {
                    // The document cannot be read, so the created directories cannot be identified; delete only the .txnew files and restage backups of the ID taken from the file name.
                    // With a different version, it may have been left by a newer library, so neither .txnew files nor restage backups are touched.
                    if (MetadataNames.TryGetTransactionId(journalPath, out Guid transactionId))
                    {
                        if (!read.UnsupportedVersion)
                        {
                            StagingApplier.DeleteStagingFiles(workFolder, transactionId);
                        }

                        reports.Add(new JournalReport(
                            transactionId,
                            RecoverResult.JournalUnreadable,
                            Array.Empty<OperationReport>()));
                    }

                    journalUnreadable = true;
                    continue;
                }

                if (document is { Committing: true })
                {
                    bool appliedAll = StagingApplier.TryApplyAll(
                        document.Operations,
                        NoFaultInjector.Instance,
                        out OperationReport[] skipped);
                    IReadOnlyList<OperationReport> operations = Array.Empty<OperationReport>();
                    RecoverResult journalResult = RecoverResult.RolledForward;
                    if (!appliedAll)
                    {
                        // Report the result only once, and do not redo it in the next Recover.
                        StagingApplier.DeleteStagingFiles(document.Operations);
                        operations = skipped;
                        journalResult = RecoverResult.ConflictDetected;
                        conflictDetected = true;
                    }

                    await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                    reports.Add(new JournalReport(document.TransactionId, journalResult, operations));
                    rolledForward |= appliedAll;
                    continue;
                }

                StagingApplier.DeleteCreateDirectoryTrees(document.Operations);
                StagingApplier.DeleteStagingFiles(document.Operations);
                StagingApplier.DeleteStagingBackups(document.Operations);
                StagingApplier.DeleteCreatedDirectories(document.CreatedDirectories);
                await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                reports.Add(new JournalReport(
                    document.TransactionId,
                    RecoverResult.RolledBack,
                    Array.Empty<OperationReport>()));
                rolledBack = true;
            }
            finally
            {
                await liveness.DisposeAsync().ConfigureAwait(false);
            }
        }

        RecoverResult result = RecoverResult.NoPendingTransactions;
        if (journalUnreadable)
        {
            result = RecoverResult.JournalUnreadable;
        }
        else if (conflictDetected)
        {
            result = RecoverResult.ConflictDetected;
        }
        else if (rolledForward)
        {
            result = RecoverResult.RolledForward;
        }
        else if (rolledBack)
        {
            result = RecoverResult.RolledBack;
        }

        return new RecoverReport(result, reports);
    }
}
