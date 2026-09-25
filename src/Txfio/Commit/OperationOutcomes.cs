namespace Txfio;

/// <summary>
/// Committing 直前に Before / After を適用順で投影する
/// </summary>
internal static class OperationOutcomes
{
    /// <summary>
    /// 全操作の Before / After を決め、投影できればその配列を返す
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="transactionId">ディレクトリ直下の検証に使うトランザクション ID</param>
    /// <param name="stamped">状態を付けた操作一覧（失敗時は空）</param>
    /// <param name="rejections">検証で拒んだ操作（成功時は空）</param>
    /// <param name="isExternalChange">記録と違う Update（または畳んだ残り）のとき <see langword="true"/>（null は比べない）</param>
    /// <returns>すべて記録できたら <see langword="true"/></returns>
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
        stamped = operation;
        reason = OperationFailureReason.Missing;
        if (!OperationKind.TryGet(operation.Kind, out OperationKind behavior))
        {
            return false;
        }

        return behavior.TryProject(operation, operations, transactionId, projected, out stamped, out reason);
    }
}
