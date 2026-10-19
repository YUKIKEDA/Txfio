namespace Txfio;

/// <summary>
/// Collects the per-journal results of one <c>RecoverAsync</c> call and decides the overall result.
/// </summary>
internal sealed class RecoverTally
{
    private readonly List<KeyValuePair<string, JournalReport>> _reports = new List<KeyValuePair<string, JournalReport>>();
    private bool _unreadable;

    /// <summary>
    /// Adds the result of one journal.
    /// </summary>
    /// <param name="journalPath">The path of the journal (the report is sorted by it).</param>
    /// <param name="report">The result of that journal.</param>
    internal void Add(string journalPath, JournalReport report)
    {
        _reports.Add(new KeyValuePair<string, JournalReport>(journalPath, report));
    }

    /// <summary>
    /// Records that a journal could not be read, even when it cannot be listed (its file name has no transaction ID).
    /// </summary>
    internal void MarkUnreadable()
    {
        _unreadable = true;
    }

    /// <summary>
    /// Returns the report, with the journals in lexical order of the path ignoring case.
    /// </summary>
    /// <returns>The overall result by priority (<see cref="RecoverResult.JournalUnreadable"/>, <see cref="RecoverResult.Contended"/>, <see cref="RecoverResult.ConflictDetected"/>, <see cref="RecoverResult.RolledForward"/>, <see cref="RecoverResult.RolledBack"/>) and the journals.</returns>
    internal RecoverReport ToReport()
    {
        JournalReport[] journals = _reports
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => pair.Value)
            .ToArray();
        RecoverResult result = RecoverResult.NoPendingTransactions;
        if (_unreadable)
        {
            result = RecoverResult.JournalUnreadable;
        }
        else if (Any(journals, RecoverResult.Contended))
        {
            result = RecoverResult.Contended;
        }
        else if (Any(journals, RecoverResult.ConflictDetected))
        {
            result = RecoverResult.ConflictDetected;
        }
        else if (Any(journals, RecoverResult.RolledForward))
        {
            result = RecoverResult.RolledForward;
        }
        else if (Any(journals, RecoverResult.RolledBack))
        {
            result = RecoverResult.RolledBack;
        }

        return new RecoverReport(result, journals);
    }

    private static bool Any(JournalReport[] journals, RecoverResult result)
    {
        return journals.Any(journal => journal.Result == result);
    }
}
