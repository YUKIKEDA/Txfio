namespace Txfio;

/// <summary>
/// An orphaned journal that <c>RecoverAsync</c> has read but not processed yet, because its footprint locks or its liveness lock could not be taken.
/// </summary>
internal sealed class PendingJournal
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PendingJournal"/> class.
    /// </summary>
    /// <param name="journalPath">The path of the journal.</param>
    /// <param name="document">The document that was read (an orphaned journal no longer changes).</param>
    internal PendingJournal(string journalPath, JournalDocument document)
    {
        JournalPath = journalPath;
        Document = document;
    }

    /// <summary>
    /// Gets the path of the journal.
    /// </summary>
    internal string JournalPath { get; }

    /// <summary>
    /// Gets the document that was read.
    /// </summary>
    internal JournalDocument Document { get; }
}
