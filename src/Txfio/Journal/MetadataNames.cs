namespace Txfio;

/// <summary>
/// Names of the metadata folder, journals, and locks.
/// </summary>
internal static class MetadataNames
{
    /// <summary>
    /// The name of the metadata folder directly under the work folder.
    /// </summary>
    internal const string FolderName = ".txfio";

    /// <summary>
    /// The search pattern for enumerating journal files.
    /// </summary>
    internal const string JournalSearchPattern = "tx-*.journal";

    /// <summary>
    /// The search pattern for enumerating journal temporary files.
    /// </summary>
    internal const string JournalTempSearchPattern = "tx-*.journal.tmp";

    /// <summary>
    /// The suffix of a journal temporary file name.
    /// </summary>
    internal const string JournalTempSuffix = ".tmp";

    /// <summary>
    /// The name of the folder that holds lock files.
    /// </summary>
    internal const string LockFolderName = "locks";

    /// <summary>
    /// Returns the absolute path of the metadata folder.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <returns>The path of the <c>.txfio</c> folder.</returns>
    internal static string FolderPath(string workFolder)
    {
        return System.IO.Path.Combine(workFolder, FolderName);
    }

    /// <summary>
    /// Returns the absolute path of the folder that holds lock files.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <returns>The path of the <c>.txfio/locks</c> folder.</returns>
    internal static string LockFolderPath(string workFolder)
    {
        return System.IO.Path.Combine(FolderPath(workFolder), LockFolderName);
    }

    /// <summary>
    /// Returns the path of the share-lost marker, opened while the work-folder lock is not held.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <returns>The path of the marker (<c>.txfio/share-lost.lock</c>).</returns>
    internal static string ShareLostLockPath(string workFolder)
    {
        return System.IO.Path.Combine(FolderPath(workFolder), "share-lost.lock");
    }

    /// <summary>
    /// Returns the path of the journal file of a transaction.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <returns>The path of the journal file.</returns>
    internal static string JournalPath(string workFolder, Guid transactionId)
    {
        return System.IO.Path.Combine(
            FolderPath(workFolder),
            "tx-" + transactionId.ToString("D") + ".journal");
    }

    /// <summary>
    /// Returns the work folder that contains a journal, from the journal's path.
    /// </summary>
    /// <param name="journalPath">The path of <c>.txfio/tx-{guid}.journal</c>.</param>
    /// <returns>The work folder that is the parent of <c>.txfio</c>.</returns>
    internal static string WorkFolderFromJournal(string journalPath)
    {
        string metadataFolder = System.IO.Path.GetDirectoryName(journalPath)!;
        return System.IO.Path.GetDirectoryName(metadataFolder)!;
    }

    /// <summary>
    /// Returns the path of the liveness lock paired with a journal.
    /// </summary>
    /// <param name="journalPath">The path of <c>.txfio/tx-{guid}.journal</c>.</param>
    /// <returns>The path of <c>.txfio/tx-{guid}.lock</c>.</returns>
    internal static string LivenessLockPath(string journalPath)
    {
        return System.IO.Path.ChangeExtension(journalPath, ".lock");
    }

    /// <summary>
    /// Returns the path of the temporary file for overwriting.
    /// </summary>
    /// <param name="journalPath">The path of <c>.txfio/tx-{guid}.journal</c>.</param>
    /// <returns>The path of <c>.txfio/tx-{guid}.journal.tmp</c>.</returns>
    internal static string JournalTempPath(string journalPath)
    {
        return journalPath + JournalTempSuffix;
    }

    /// <summary>
    /// Gets the path of the journal paired with a temporary file.
    /// </summary>
    /// <param name="tempPath">The path of <c>.txfio/tx-{guid}.journal.tmp</c>.</param>
    /// <param name="journalPath">The path of <c>.txfio/tx-{guid}.journal</c>.</param>
    /// <returns><see langword="true"/> if it is the name of a journal temporary file.</returns>
    internal static bool TryGetJournalPathFromTemp(string tempPath, out string journalPath)
    {
        journalPath = string.Empty;
        if (!tempPath.EndsWith(".journal" + JournalTempSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        journalPath = tempPath.Substring(0, tempPath.Length - JournalTempSuffix.Length);
        return TryGetTransactionId(journalPath, out _);
    }

    /// <summary>
    /// Gets the transaction ID from a journal's file name.
    /// </summary>
    /// <param name="journalPath">The path of <c>.txfio/tx-{guid}.journal</c>.</param>
    /// <param name="transactionId">The ID that was found.</param>
    /// <returns><see langword="true"/> if the file name is <c>tx-{guid}.journal</c>.</returns>
    internal static bool TryGetTransactionId(string journalPath, out Guid transactionId)
    {
        string name = System.IO.Path.GetFileName(journalPath);
        const string prefix = "tx-";
        const string suffix = ".journal";
        if (name.Length > prefix.Length + suffix.Length
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string id = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            return Guid.TryParseExact(id, "D", out transactionId);
        }

        transactionId = Guid.Empty;
        return false;
    }
}
