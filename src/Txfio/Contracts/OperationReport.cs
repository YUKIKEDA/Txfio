namespace Txfio;

/// <summary>
/// One operation that could not be finished.
/// </summary>
public sealed class OperationReport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OperationReport"/> class with the path and the reason.
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <param name="newPath">The Move destination (<see langword="null"/> otherwise).</param>
    /// <param name="kind">The operation kind.</param>
    /// <param name="disposition">Whether it was rejected by the check or skipped during apply.</param>
    /// <param name="reason">The reason for the failure.</param>
    public OperationReport(
        string path,
        string? newPath,
        PendingChangeKind kind,
        OperationDisposition disposition,
        OperationFailureReason reason)
    {
        Path = path;
        NewPath = newPath;
        Kind = kind;
        Disposition = disposition;
        Reason = reason;
    }

    /// <summary>
    /// Gets the target path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the Move destination path.
    /// </summary>
    public string? NewPath { get; }

    /// <summary>
    /// Gets the operation kind.
    /// </summary>
    public PendingChangeKind Kind { get; }

    /// <summary>
    /// Gets the disposition: whether it was rejected by the check or skipped during apply.
    /// </summary>
    public OperationDisposition Disposition { get; }

    /// <summary>
    /// Gets the reason for the failure.
    /// </summary>
    public OperationFailureReason Reason { get; }

    /// <summary>
    /// Creates the report of one operation that could not be finished from a journal operation.
    /// </summary>
    /// <param name="operation">The target operation.</param>
    /// <param name="disposition">Whether it was rejected by the check or skipped during apply.</param>
    /// <param name="reason">The reason for the failure.</param>
    /// <returns>A report with the path and the reason.</returns>
    internal static OperationReport Create(
        JournalOperation operation,
        OperationDisposition disposition,
        OperationFailureReason reason)
    {
        string? newPath = operation.Kind == PendingChangeKind.Move ? operation.NewPath : null;
        return new OperationReport(operation.Path, newPath, operation.Kind, disposition, reason);
    }
}
