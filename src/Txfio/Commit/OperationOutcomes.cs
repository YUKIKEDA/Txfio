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
        if (operation.Kind == PendingChangeKind.Add)
        {
            return TryProjectAdd(operation, projected, out stamped, out reason);
        }

        if (operation.Kind == PendingChangeKind.Update)
        {
            return TryProjectUpdate(operation, projected, out stamped, out reason);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            return TryProjectMove(operation, projected, out stamped, out reason);
        }

        if (operation.Kind == PendingChangeKind.CreateDirectory)
        {
            return TryProjectCreateDirectory(operation, projected, out stamped, out reason);
        }

        if (operation.Kind == PendingChangeKind.DeleteTree)
        {
            return TryProjectDeleteTree(operation, projected, out stamped, out reason);
        }

        if (operation.Kind == PendingChangeKind.Delete)
        {
            return TryProjectDelete(operation, operations, transactionId, projected, out stamped, out reason);
        }

        return false;
    }

    private static bool TryProjectAdd(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        PathState before = Current(projected, operation.Path);
        if (before.Exists)
        {
            reason = OperationFailureReason.AlreadyExists;
            return false;
        }

        if (!TryCaptureStaging(operation, out PathState after))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        projected[operation.Path] = after;
        stamped = operation.WithOutcome(before, after);
        return true;
    }

    private static bool TryProjectUpdate(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        PathState before = Current(projected, operation.Path);
        if (!before.IsFile)
        {
            reason = ReasonWhenFileRequired(before);
            return false;
        }

        if (!TryCaptureStaging(operation, out PathState after))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        projected[operation.Path] = after;
        stamped = operation.WithOutcome(before, after);
        return true;
    }

    private static bool TryProjectMove(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        if (string.IsNullOrEmpty(operation.NewPath))
        {
            return false;
        }

        PathState before = Current(projected, operation.Path);
        PathState destBefore = Current(projected, operation.NewPath);
        if (operation.IsDirectory)
        {
            if (!before.IsDirectory)
            {
                reason = ReasonWhenDirectoryRequired(before);
                return false;
            }

            if (destBefore.Exists)
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            projected[operation.Path] = PathState.Absent;
            projected[operation.NewPath] = before;
            stamped = operation.WithOutcome(before, PathState.Absent, PathState.Absent, before);
            return true;
        }

        if (!before.IsFile)
        {
            reason = ReasonWhenFileRequired(before);
            return false;
        }

        if (destBefore.Exists)
        {
            reason = OperationFailureReason.AlreadyExists;
            return false;
        }

        projected[operation.Path] = PathState.Absent;
        projected[operation.NewPath] = before;
        stamped = operation.WithOutcome(before, PathState.Absent, PathState.Absent, before);
        return true;
    }

    private static bool TryProjectCreateDirectory(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        PathState current = Current(projected, operation.Path);
        if (!current.IsDirectory)
        {
            reason = ReasonWhenDirectoryRequired(current);
            return false;
        }

        projected[operation.Path] = current;
        stamped = operation.WithOutcome(current, current);
        return true;
    }

    private static bool TryProjectDeleteTree(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        PathState before = Current(projected, operation.Path);
        if (!before.IsDirectory)
        {
            reason = ReasonWhenDirectoryRequired(before);
            return false;
        }

        projected[operation.Path] = PathState.Absent;
        stamped = operation.WithOutcome(before, PathState.Absent);
        return true;
    }

    private static bool TryProjectDelete(
        JournalOperation operation,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped,
        out OperationFailureReason reason)
    {
        stamped = operation;
        reason = OperationFailureReason.Missing;
        PathState before = Current(projected, operation.Path);
        if (operation.IsDirectory)
        {
            if (!before.IsDirectory)
            {
                reason = ReasonWhenDirectoryRequired(before);
                return false;
            }

            if (!StagingRules.MatchesDirectoryDeletePreconditions(operation.Path, operations, transactionId))
            {
                reason = OperationFailureReason.DirectoryPreconditions;
                return false;
            }
        }
        else if (!before.IsFile)
        {
            reason = ReasonWhenFileRequired(before);
            return false;
        }

        projected[operation.Path] = PathState.Absent;
        stamped = operation.WithOutcome(before, PathState.Absent);
        return true;
    }

    private static bool TryCaptureStaging(JournalOperation operation, out PathState after)
    {
        if (string.IsNullOrEmpty(operation.StagingPath))
        {
            after = PathState.Absent;
            return false;
        }

        after = PathState.Capture(operation.StagingPath);
        return after.IsFile;
    }

    private static PathState Current(Dictionary<string, PathState> projected, string path)
    {
        if (projected.TryGetValue(path, out PathState? state))
        {
            return state;
        }

        return PathState.Capture(path);
    }

    private static OperationFailureReason ReasonWhenFileRequired(PathState state)
    {
        if (!state.Exists)
        {
            return OperationFailureReason.Missing;
        }

        return state.IsDirectory ? OperationFailureReason.ReplacedByFile : OperationFailureReason.AlreadyExists;
    }

    private static OperationFailureReason ReasonWhenDirectoryRequired(PathState state)
    {
        if (!state.Exists)
        {
            return OperationFailureReason.Missing;
        }

        return state.IsFile ? OperationFailureReason.ReplacedByFile : OperationFailureReason.AlreadyExists;
    }
}
