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
    /// <returns>すべて記録できたら <see langword="true"/></returns>
    internal static bool TryStamp(
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        out JournalOperation[] stamped)
    {
        JournalOperation[] result = operations.ToArray();
        Dictionary<JournalOperation, int> indexByOperation = new Dictionary<JournalOperation, int>(
            ReferenceEqualityComparer.Instance);
        for (int i = 0; i < result.Length; i++)
        {
            indexByOperation.Add(result[i], i);
        }

        Dictionary<string, PathState> projected = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalOperation operation in StagingApplier.InApplyOrder(result))
        {
            if (!TryProject(operation, result, transactionId, projected, out JournalOperation next))
            {
                stamped = Array.Empty<JournalOperation>();
                return false;
            }

            result[indexByOperation[operation]] = next;
        }

        stamped = result;
        return true;
    }

    private static bool TryProject(
        JournalOperation operation,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped)
    {
        stamped = operation;
        if (operation.Kind == PendingChangeKind.Add)
        {
            return TryProjectAdd(operation, projected, out stamped);
        }

        if (operation.Kind == PendingChangeKind.Update)
        {
            return TryProjectUpdate(operation, projected, out stamped);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            return TryProjectMove(operation, projected, out stamped);
        }

        if (operation.Kind == PendingChangeKind.CreateDirectory)
        {
            return TryProjectCreateDirectory(operation, projected, out stamped);
        }

        if (operation.Kind == PendingChangeKind.DeleteTree)
        {
            return TryProjectDeleteTree(operation, projected, out stamped);
        }

        if (operation.Kind == PendingChangeKind.Delete)
        {
            return TryProjectDelete(operation, operations, transactionId, projected, out stamped);
        }

        return false;
    }

    private static bool TryProjectAdd(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped)
    {
        stamped = operation;
        PathState before = Current(projected, operation.Path);
        if (before.Exists || !TryCaptureStaging(operation, out PathState after))
        {
            return false;
        }

        projected[operation.Path] = after;
        stamped = operation.WithOutcome(before, after);
        return true;
    }

    private static bool TryProjectUpdate(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped)
    {
        stamped = operation;
        PathState before = Current(projected, operation.Path);
        if (!before.IsFile || !TryCaptureStaging(operation, out PathState after))
        {
            return false;
        }

        projected[operation.Path] = after;
        stamped = operation.WithOutcome(before, after);
        return true;
    }

    private static bool TryProjectMove(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped)
    {
        stamped = operation;
        if (string.IsNullOrEmpty(operation.NewPath))
        {
            return false;
        }

        PathState before = Current(projected, operation.Path);
        PathState destBefore = Current(projected, operation.NewPath);
        if (operation.IsDirectory)
        {
            if (!before.IsDirectory || destBefore.Exists)
            {
                return false;
            }

            projected[operation.Path] = PathState.Absent;
            projected[operation.NewPath] = before;
            stamped = operation.WithOutcome(before, PathState.Absent, PathState.Absent, before);
            return true;
        }

        if (!before.IsFile || destBefore.Exists)
        {
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
        out JournalOperation stamped)
    {
        stamped = operation;
        PathState current = Current(projected, operation.Path);
        if (!current.IsDirectory)
        {
            return false;
        }

        projected[operation.Path] = current;
        stamped = operation.WithOutcome(current, current);
        return true;
    }

    private static bool TryProjectDeleteTree(
        JournalOperation operation,
        Dictionary<string, PathState> projected,
        out JournalOperation stamped)
    {
        stamped = operation;
        PathState before = Current(projected, operation.Path);
        if (!before.IsDirectory)
        {
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
        out JournalOperation stamped)
    {
        stamped = operation;
        PathState before = Current(projected, operation.Path);
        if (operation.IsDirectory)
        {
            if (!before.IsDirectory
                || !StagingRules.MatchesDirectoryDeletePreconditions(operation.Path, operations, transactionId))
            {
                return false;
            }
        }
        else if (!before.IsFile)
        {
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
}
