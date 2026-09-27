namespace Txfio;

/// <summary>
/// One recovered journal.
/// </summary>
public sealed class JournalReport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalReport"/> class with the transaction and the result of its journal.
    /// </summary>
    /// <param name="transactionId">The transaction ID of the journal.</param>
    /// <param name="result">The result of that journal.</param>
    /// <param name="operations">The operations skipped because of conflicts (empty when there is no conflict, and when the journal is unreadable).</param>
    public JournalReport(Guid transactionId, RecoverResult result, IReadOnlyList<OperationReport> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        TransactionId = transactionId;
        Result = result;
        Operations = operations;
    }

    /// <summary>
    /// Gets the transaction ID of the journal.
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// Gets the result of that journal.
    /// </summary>
    public RecoverResult Result { get; }

    /// <summary>
    /// Gets the operations skipped because of conflicts (empty when there is no conflict, and when the journal is unreadable).
    /// </summary>
    public IReadOnlyList<OperationReport> Operations { get; }
}
