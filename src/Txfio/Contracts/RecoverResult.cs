namespace Txfio;

/// <summary>
/// The value of <see cref="RecoverReport.Result"/>.
/// </summary>
public enum RecoverResult
{
    /// <summary>
    /// There were no unfinished transactions.
    /// </summary>
    NoPendingTransactions = 0,

    /// <summary>
    /// An uncommitted journal was discarded and rolled back.
    /// </summary>
    RolledBack = 1,

    /// <summary>
    /// The operations of a Committing journal were rolled forward.
    /// </summary>
    RolledForward = 2,

    /// <summary>
    /// An operation matched neither Before nor After.
    /// </summary>
    ConflictDetected = 3,

    /// <summary>
    /// A journal could not be read as JSON.
    /// </summary>
    JournalUnreadable = 4,
}
