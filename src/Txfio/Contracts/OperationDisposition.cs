namespace Txfio;

/// <summary>
/// What happened to an operation that could not be finished.
/// </summary>
public enum OperationDisposition
{
    /// <summary>
    /// Rejected by the check.
    /// </summary>
    Rejected = 0,

    /// <summary>
    /// Skipped during apply.
    /// </summary>
    Skipped = 1,
}
