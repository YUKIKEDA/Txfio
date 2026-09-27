namespace Txfio;

/// <summary>
/// The value of <see cref="CommitReport.Result"/>.
/// </summary>
public enum CommitResult
{
    /// <summary>
    /// Every operation was applied as expected.
    /// </summary>
    Succeeded = 0,

    /// <summary>
    /// Some operations met external interference, but the commit was finished.
    /// </summary>
    PartialConflict = 1,

    /// <summary>
    /// The check before commit failed, and nothing on disk was touched.
    /// </summary>
    Failed = 2,
}
