namespace Txfio;

/// <summary>
/// ステージングした操作を対象パスへ適用する（Move の連鎖は空いている端から、ファイルの移動元への Add はその Move のあと）
/// </summary>
internal static class StagingApplier
{
    /// <summary>
    /// Add / Move / CreateDirectory を先に、Update を次に、Delete と DeleteTree をパスが深い順で後に適用する（Move の連鎖は空いている端から、ファイルの移動元への Add はその Move のあと）
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <param name="faults">この適用の失敗と途中停止</param>
    /// <param name="skipped">適用で飛ばした操作（すべてできたときは空）</param>
    /// <returns>すべて適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApplyAll(
        IReadOnlyList<JournalOperation> operations,
        IFaultInjector faults,
        out OperationReport[] skipped)
    {
        List<OperationReport> failures = new List<OperationReport>();
        foreach (JournalOperation operation in InApplyOrder(operations))
        {
            if (!TryApply(operation, faults, out OperationFailureReason reason))
            {
                failures.Add(OperationReport.Create(operation, OperationDisposition.Skipped, reason));
                continue;
            }

            faults.CheckPoint(IFaultInjector.AfterApply);
        }

        skipped = failures.ToArray();
        return failures.Count == 0;
    }

    /// <summary>
    /// Add / Move / CreateDirectory、Update、Delete と DeleteTree（深い順）の順に並べる（Move の連鎖は空いている端から、ファイルの移動元への Add はその Move のあと）
    /// </summary>
    /// <param name="operations">操作一覧</param>
    /// <returns>適用順の操作</returns>
    internal static JournalOperation[] InApplyOrder(IReadOnlyList<JournalOperation> operations)
    {
        List<JournalOperation> ordered = new List<JournalOperation>(operations.Count);
        List<JournalOperation> nondestructive = new List<JournalOperation>();
        foreach (JournalOperation operation in operations)
        {
            if (OperationKind.For(operation.Kind).ApplyPhase == OperationKind.Phase.Nondestructive)
            {
                nondestructive.Add(operation);
            }
        }

        ordered.AddRange(OrderMoveChains(nondestructive));

        foreach (JournalOperation operation in operations)
        {
            if (OperationKind.For(operation.Kind).ApplyPhase == OperationKind.Phase.Update)
            {
                ordered.Add(operation);
            }
        }

        List<JournalOperation> deletes = new List<JournalOperation>();
        foreach (JournalOperation operation in operations)
        {
            if (OperationKind.For(operation.Kind).ApplyPhase == OperationKind.Phase.Delete)
            {
                deletes.Add(operation);
            }
        }

        deletes.Sort(static (left, right) => PathMath.Depth(right.Path).CompareTo(PathMath.Depth(left.Path)));
        ordered.AddRange(deletes);
        return ordered.ToArray();
    }

    /// <summary>
    /// すべての Move を、空いている端から辿れるかを判定する
    /// </summary>
    /// <param name="operations">操作一覧</param>
    /// <returns>循環が無ければ <see langword="true"/></returns>
    internal static bool MovesReachFreeEnd(IReadOnlyList<JournalOperation> operations)
    {
        List<JournalOperation> moves = new List<JournalOperation>();
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Move)
            {
                moves.Add(operation);
            }
        }

        return TryOrderMoves(moves, out _);
    }

    /// <summary>
    /// 1 操作を適用する（既に適用済みなら成功、失敗なら <see langword="false"/>）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <param name="faults">この適用の失敗と途中停止</param>
    /// <param name="reason">飛ばした理由（成功時は使わない）</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation, IFaultInjector faults, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        faults.ThrowIfApplyArmed();
        if (operation.Before is null || operation.After is null)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        if (operation.Kind == PendingChangeKind.Move
            && (string.IsNullOrEmpty(operation.NewPath)
                || operation.DestBefore is null
                || operation.DestAfter is null))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        return OperationKind.For(operation.Kind).TryApply(operation, out reason);
    }

    /// <summary>
    /// 操作ごとの `.txnew` を消す（無いものは飛ばす）
    /// </summary>
    /// <param name="operations">操作一覧</param>
    internal static void DeleteStagingFiles(IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            OperationKind.DeleteStaging(operation);
        }
    }

    /// <summary>
    /// このトランザクションの `.txnew` を、ワークフォルダ配下から消す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="transactionId">トランザクション ID</param>
    internal static void DeleteStagingFiles(string workFolder, Guid transactionId)
    {
        string pattern = "*." + transactionId.ToString("D") + ".txnew";
        foreach (string path in Directory.EnumerateFiles(workFolder, pattern, SearchOption.AllDirectories))
        {
            if (WorkPath.IsInMetadataFolder(workFolder, path)
                || !WorkPath.IsThisTransactionStagingFile(path, transactionId))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// 再ステージの退避 <c>.txnew.prev</c> を、ワークフォルダ配下から消す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <param name="ignoreIoFailures"><see langword="true"/> なら <see cref="IOException"/> と <see cref="UnauthorizedAccessException"/> を投げずに最後まで続ける</param>
    /// <returns>すべて消せたら <see langword="true"/>（例外を投げるときは戻らない）</returns>
    internal static bool DeleteStagingBackups(string workFolder, Guid transactionId, bool ignoreIoFailures = false)
    {
        try
        {
            bool succeeded = true;
            string suffix = "." + transactionId.ToString("D") + ".txnew.prev";
            string pattern = "*" + suffix;
            foreach (string path in Directory.EnumerateFiles(workFolder, pattern, SearchOption.AllDirectories))
            {
                if (WorkPath.IsInMetadataFolder(workFolder, path)
                    || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IoErrors.TryDelete(ignoreIoFailures, () => File.Delete(path)))
                {
                    succeeded = false;
                }
            }

            return succeeded;
        }
        catch (Exception exception) when (ignoreIoFailures && IoErrors.IsIo(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// 作成ディレクトリを深い順に、再帰せず消す
    /// </summary>
    /// <param name="directories">消すディレクトリ</param>
    /// <param name="ignoreIoFailures"><see langword="true"/> なら <see cref="IOException"/> と <see cref="UnauthorizedAccessException"/> を投げずに最後まで続ける</param>
    /// <returns>すべて消せたら <see langword="true"/>（例外を投げるときは戻らない）</returns>
    internal static bool DeleteCreatedDirectories(IReadOnlyList<string> directories, bool ignoreIoFailures = false)
    {
        List<string> pending = new List<string>(directories);
        PathMath.SortDeepestFirst(pending);

        bool succeeded = true;
        foreach (string path in pending)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            if (!IoErrors.TryDelete(ignoreIoFailures, () => Directory.Delete(path)))
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    /// <summary>
    /// CreateDirectory が作ったディレクトリを、中身ごと消す
    /// </summary>
    /// <param name="operations">操作一覧</param>
    /// <param name="ignoreIoFailures"><see langword="true"/> なら <see cref="IOException"/> と <see cref="UnauthorizedAccessException"/> を投げずに最後まで続ける</param>
    /// <returns>すべて消せたら <see langword="true"/>（例外を投げるときは戻らない）</returns>
    internal static bool DeleteCreateDirectoryTrees(IReadOnlyList<JournalOperation> operations, bool ignoreIoFailures = false)
    {
        bool succeeded = true;
        foreach (JournalOperation operation in operations)
        {
            if (!OperationKind.For(operation.Kind).TryDeleteCreatedTree(operation, ignoreIoFailures))
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private static List<JournalOperation> OrderMoveChains(List<JournalOperation> items)
    {
        List<int> moveSlots = new List<int>();
        List<JournalOperation> moves = new List<JournalOperation>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Kind == PendingChangeKind.Move)
            {
                moveSlots.Add(i);
                moves.Add(items[i]);
            }
        }

        if (!TryOrderMoves(moves, out List<JournalOperation> orderedMoves))
        {
            return items;
        }

        JournalOperation[] placed = items.ToArray();
        for (int i = 0; i < moveSlots.Count; i++)
        {
            placed[moveSlots[i]] = orderedMoves[i];
        }

        HashSet<string> fileMoveSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalOperation operation in placed)
        {
            if (operation.Kind == PendingChangeKind.Move && !operation.IsDirectory)
            {
                fileMoveSources.Add(operation.Path);
            }
        }

        HashSet<string> emittedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<JournalOperation> output = new List<JournalOperation>(placed.Length);
        List<JournalOperation> heldAdds = new List<JournalOperation>();
        foreach (JournalOperation operation in placed)
        {
            if (operation.Kind == PendingChangeKind.Add
                && fileMoveSources.Contains(operation.Path)
                && !emittedSources.Contains(operation.Path))
            {
                heldAdds.Add(operation);
                continue;
            }

            output.Add(operation);
            if (operation.Kind != PendingChangeKind.Move || operation.IsDirectory)
            {
                continue;
            }

            emittedSources.Add(operation.Path);
            for (int i = heldAdds.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(heldAdds[i].Path, operation.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                output.Add(heldAdds[i]);
                heldAdds.RemoveAt(i);
            }
        }

        output.AddRange(heldAdds);
        return output;
    }

    private static bool TryOrderMoves(List<JournalOperation> moves, out List<JournalOperation> ordered)
    {
        ordered = new List<JournalOperation>(moves.Count);
        List<JournalOperation> remaining = new List<JournalOperation>(moves);
        while (remaining.Count > 0)
        {
            int ready = -1;
            for (int i = 0; i < remaining.Count; i++)
            {
                if (HasFreeDestination(remaining, i))
                {
                    ready = i;
                    break;
                }
            }

            if (ready < 0)
            {
                ordered.Clear();
                return false;
            }

            ordered.Add(remaining[ready]);
            remaining.RemoveAt(ready);
        }

        return true;
    }

    private static bool HasFreeDestination(List<JournalOperation> remaining, int index)
    {
        string? dest = remaining[index].NewPath;
        if (string.IsNullOrEmpty(dest)
            || string.Equals(remaining[index].Path, dest, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 0; i < remaining.Count; i++)
        {
            if (i != index && string.Equals(remaining[i].Path, dest, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
