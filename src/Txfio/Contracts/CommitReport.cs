namespace Txfio;

/// <summary>
/// The result of <see cref="ITransaction.CommitAsync"/>.
/// </summary>
public sealed class CommitReport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommitReport"/> class with the overall result and the operations that could not be finished.
    /// </summary>
    /// <param name="result">The overall result.</param>
    /// <param name="operations">The operations that were rejected or skipped (empty on success).</param>
    public CommitReport(CommitResult result, IReadOnlyList<OperationReport> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Result = result;
        Operations = operations;
    }

    /// <summary>
    /// Gets the overall result.
    /// </summary>
    public CommitResult Result { get; }

    /// <summary>
    /// Gets the operations that were rejected or skipped (empty on success).
    /// </summary>
    public IReadOnlyList<OperationReport> Operations { get; }
}
