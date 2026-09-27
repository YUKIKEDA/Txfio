namespace Txfio;

/// <summary>
/// Rejects new transactions while an orphaned journal exists.
/// </summary>
internal static class StaleJournals
{
    /// <summary>
    /// Throws if there is an orphaned journal.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <exception cref="RecoveryRequiredException">There is an orphaned journal.</exception>
    internal static void ThrowIfAny(string workFolder)
    {
        if (Exists(workFolder))
        {
            throw new RecoveryRequiredException(
                "A transaction has not been recovered. Call RecoverAsync first: " + workFolder,
                workFolder);
        }
    }

    /// <summary>
    /// Returns whether some journal's liveness lock can be opened and the journal still exists after that (journals are not read).
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <returns><see langword="true"/> if there is an orphaned journal.</returns>
    internal static bool Exists(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return false;
        }

        foreach (string journalPath in Directory.GetFiles(
            metadataFolder,
            MetadataNames.JournalSearchPattern,
            SearchOption.TopDirectoryOnly))
        {
            // A sharing violation means the owner is alive (our own journal always skips here, since we hold its lock).
            using FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is not null && File.Exists(journalPath))
            {
                return true;
            }
        }

        return false;
    }
}
