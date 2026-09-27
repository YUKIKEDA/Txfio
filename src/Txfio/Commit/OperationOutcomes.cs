namespace Txfio;

/// <summary>
/// Projects Before / After in apply order just before Committing.
/// </summary>
internal static class OperationOutcomes
{
    /// <summary>
    /// Decides Before / After for every operation, and returns the array if it can be projected.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="transactionId">The transaction ID used to check direct children of directories.</param>
    /// <param name="stamped">The operations with their states (empty on failure).</param>
    /// <param name="rejections">The operations rejected by the check (empty on success).</param>
    /// <param name="isExternalChange">Returns <see langword="true"/> for an Update (or what remains after folding) that differs from the record (<see langword="null"/> does not compare).</param>
    /// <returns><see langword="true"/> if everything was recorded.</returns>
    internal static bool TryStamp(
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        out JournalOperation[] stamped,
        out OperationReport[] rejections,
        Func<JournalOperation, bool>? isExternalChange = null)
    {
        JournalOperation[] result = operations.ToArray();
        Dictionary<JournalOperation, int> indexByOperation = new Dictionary<JournalOperation, int>(
            ReferenceEqualityComparer.Instance);
        for (int i = 0; i < result.Length; i++)
        {
            indexByOperation.Add(result[i], i);
        }

        List<OperationReport> rejected = new List<OperationReport>();
        Dictionary<string, PathState> projected = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalOperation operation in StagingApplier.InApplyOrder(result))
        {
            if (!TryProject(operation, result, transactionId, projected, out JournalOperation next, out OperationFailureReason reason))
            {
                rejected.Add(OperationReport.Create(operation, OperationDisposition.Rejected, reason));
                continue;
            }

            if (isExternalChange is not null && isExternalChange(operation))
            {
                rejected.Add(OperationReport.Create(
                    operation,
                    OperationDisposition.Rejected,
                    OperationFailureReason.ExternalChange));
                continue;
            }

            result[indexByOperation[operation]] = next;
        }

        if (rejected.Count > 0)
        {
            stamped = Array.Empty<JournalOperation>();
            rejections = rejected.ToArray();
            return false;
        }

        stamped = result;
        rejections = Array.Empty<OperationReport>();
        return true;
    }

    private static bool TryProject(
        JournalOperation operation,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        return OperationKind.For(operation.Kind).TryProject(operation, operations, transactionId, projected, out stamped, out reason);
    }
}
