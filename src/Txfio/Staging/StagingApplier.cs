namespace Txfio;

/// <summary>
/// Applies staged operations to their target paths (Move chains from the free end; an Add to the source of a file comes after that Move).
/// </summary>
internal static class StagingApplier
{
    /// <summary>
    /// Applies Add / Move / CreateDirectory first, then Update, and last Delete and DeleteTree, deepest path first (Move chains from the free end; an Add to the source of a file comes after that Move).
    /// </summary>
    /// <param name="operations">The operations to apply.</param>
    /// <param name="faults">The failures and partial stops of this apply.</param>
    /// <param name="skipped">The operations skipped during apply (empty when everything succeeded).</param>
    /// <returns><see langword="true"/> if everything was applied, or was already applied.</returns>
    internal static bool TryApplyAll(
        IReadOnlyList<JournalOperation> operations,
        IFaultInjector faults,
        out OperationReport[] skipped)
    {
        List<OperationReport> failures = new List<OperationReport>();
        JournalOperation[] ordered = InApplyOrder(operations);
        Dictionary<string, int> lastTouch = LastTouchByPath(ordered);
        for (int i = 0; i < ordered.Length; i++)
        {
            JournalOperation operation = ordered[i];
            int index = i;

            // If a later directory Move is done, the operations under its source are done too (nothing remains in the original place).
            if (IsUnderAppliedLaterDirectoryMove(ordered, i))
            {
                continue;
            }

            // A path that a later operation changes is no clue as to whether this operation is done.
            bool ChangedLater(string path) => lastTouch.TryGetValue(path, out int last) && last > index;
            if (!TryApply(operation, faults, ChangedLater, out OperationFailureReason reason))
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
    /// Orders operations as Add / Move / CreateDirectory, Update, then Delete and DeleteTree (deepest first) (Move chains from the free end; an Add to the source of a file comes after that Move).
    /// </summary>
    /// <param name="operations">The list of operations.</param>
    /// <returns>The operations in apply order.</returns>
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

        ordered.AddRange(PlaceBeforeDirectoryMoves(OrderMoveChains(nondestructive)));

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
    /// Returns whether every Move can be reached from a free end.
    /// </summary>
    /// <param name="operations">The list of operations.</param>
    /// <returns><see langword="true"/> if there is no cycle.</returns>
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
    /// Applies one operation (success if it was already applied; <see langword="false"/> on failure).
    /// </summary>
    /// <param name="operation">The operation to apply.</param>
    /// <param name="faults">The failures and partial stops of this apply.</param>
    /// <param name="changedLater"><see langword="true"/> if a later operation in apply order also changes the path.</param>
    /// <param name="reason">The reason it was skipped (unused on success).</param>
    /// <returns><see langword="true"/> if it was applied, or was already applied.</returns>
    internal static bool TryApply(
        JournalOperation operation,
        IFaultInjector faults,
        Func<string, bool> changedLater,
        out OperationFailureReason reason)
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

        return OperationKind.For(operation.Kind).TryApply(operation, changedLater, out reason);
    }

    /// <summary>
    /// Deletes the <c>.txnew</c> of each operation (skips missing ones).
    /// </summary>
    /// <param name="operations">The list of operations.</param>
    internal static void DeleteStagingFiles(IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            OperationKind.DeleteStaging(operation);
        }
    }

    /// <summary>
    /// Finds and deletes this transaction's <c>.txnew</c> files and restage backups under the work folder (used only for an unreadable journal, whose operations are unknown).
    /// </summary>
    /// <remarks>
    /// Unreadable folders and reparse points are skipped and not followed.
    /// </remarks>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="transactionId">The transaction ID.</param>
    internal static void DeleteStagingFiles(string workFolder, Guid transactionId)
    {
        string stagingSuffix = "." + transactionId.ToString("D") + ".txnew";
        string backupSuffix = WorkPath.StagingBackupPath(stagingSuffix);
        EnumerationOptions options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        foreach (string path in Directory.EnumerateFiles(workFolder, "*" + stagingSuffix + "*", options))
        {
            if (WorkPath.IsInMetadataFolder(workFolder, path)
                || !(path.EndsWith(stagingSuffix, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(backupSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// Deletes the restage backups, which are the operations' <c>.txnew</c> with <c>.prev</c> appended (the work folder is not walked).
    /// </summary>
    /// <param name="operations">The list of operations.</param>
    /// <param name="ignoreIoFailures">When <see langword="true"/>, continue to the end without throwing <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    /// <returns><see langword="true"/> if everything was deleted (does not return when it throws).</returns>
    internal static bool DeleteStagingBackups(IReadOnlyList<JournalOperation> operations, bool ignoreIoFailures = false)
    {
        bool succeeded = true;
        foreach (JournalOperation operation in operations)
        {
            if (string.IsNullOrEmpty(operation.StagingPath))
            {
                continue;
            }

            string backupPath = WorkPath.StagingBackupPath(operation.StagingPath);
            if (!IoErrors.TryDelete(ignoreIoFailures, () => StagingFile.TryDelete(backupPath)))
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    /// <summary>
    /// Deletes created directories deepest first, without recursion.
    /// </summary>
    /// <param name="directories">The directories to delete.</param>
    /// <param name="ignoreIoFailures">When <see langword="true"/>, continue to the end without throwing <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    /// <returns><see langword="true"/> if everything was deleted (does not return when it throws).</returns>
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
    /// Deletes the directories CreateDirectory created, with their contents.
    /// </summary>
    /// <param name="operations">The list of operations.</param>
    /// <param name="ignoreIoFailures">When <see langword="true"/>, continue to the end without throwing <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    /// <returns><see langword="true"/> if everything was deleted (does not return when it throws).</returns>
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

    private static bool IsUnderAppliedLaterDirectoryMove(JournalOperation[] ordered, int index)
    {
        string path = ordered[index].Path;
        for (int j = index + 1; j < ordered.Length; j++)
        {
            JournalOperation later = ordered[j];
            if (later.Kind == PendingChangeKind.Move
                && later.IsDirectory
                && !string.IsNullOrEmpty(later.NewPath)
                && PathMath.IsUnder(later.Path, path)
                && !Directory.Exists(later.Path)
                && Directory.Exists(later.NewPath))
            {
                return true;
            }
        }

        return false;
    }

    // Operations under the source of a directory Move (Adds into a created directory) are applied before that Move.
    private static List<JournalOperation> PlaceBeforeDirectoryMoves(List<JournalOperation> items)
    {
        List<JournalOperation> output = new List<JournalOperation>(items);
        for (int m = 0; m < output.Count; m++)
        {
            JournalOperation move = output[m];
            if (move.Kind != PendingChangeKind.Move || !move.IsDirectory)
            {
                continue;
            }

            for (int k = m + 1; k < output.Count; k++)
            {
                if (output[k].Kind != PendingChangeKind.Move && PathMath.IsUnder(move.Path, output[k].Path))
                {
                    JournalOperation child = output[k];
                    output.RemoveAt(k);
                    output.Insert(m, child);
                    m++;
                }
            }
        }

        return output;
    }

    private static Dictionary<string, int> LastTouchByPath(JournalOperation[] ordered)
    {
        Dictionary<string, int> lastTouch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Length; i++)
        {
            lastTouch[ordered[i].Path] = i;
            if (ordered[i].Kind == PendingChangeKind.Move && !string.IsNullOrEmpty(ordered[i].NewPath))
            {
                lastTouch[ordered[i].NewPath!] = i;
            }
        }

        return lastTouch;
    }
}
