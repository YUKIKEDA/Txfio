namespace Txfio;

/// <summary>
/// ステージングした操作を対象パスへ適用する（Move の連鎖は空いている端から、ファイルの移動元への Add はその Move のあと）
/// </summary>
internal static class StagingApplier
{
    private static readonly AsyncLocal<ApplyFailure?> _nextApplyFailure = new AsyncLocal<ApplyFailure?>();

    /// <summary>
    /// 次の適用で、指定した例外を投げる。テスト用
    /// </summary>
    /// <param name="exception">投げる例外</param>
    internal static void FailNextApply(Exception exception)
    {
        _nextApplyFailure.Value = new ApplyFailure(exception);
    }

    /// <summary>
    /// テストが仕込んだ適用の失敗を消す
    /// </summary>
    internal static void ClearApplyFailure()
    {
        ApplyFailure? failure = _nextApplyFailure.Value;
        if (failure is not null)
        {
            failure.Exception = null;
        }

        _nextApplyFailure.Value = null;
    }

    /// <summary>
    /// Add / Move / CreateDirectory を先に、Update を次に、Delete と DeleteTree をパスが深い順で後に適用する（Move の連鎖は空いている端から、ファイルの移動元への Add はその Move のあと）
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <param name="skipped">適用で飛ばした操作（すべてできたときは空）</param>
    /// <returns>すべて適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApplyAll(IReadOnlyList<JournalOperation> operations, out OperationReport[] skipped)
    {
        List<OperationReport> failures = new List<OperationReport>();
        foreach (JournalOperation operation in InApplyOrder(operations))
        {
            if (!TryApply(operation, out OperationFailureReason reason))
            {
                failures.Add(OperationReport.Create(operation, OperationDisposition.Skipped, reason));
                continue;
            }

            CrashInjector.CheckPoint(CrashInjector.AfterApply);
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
            if (operation.Kind == PendingChangeKind.Add
                || operation.Kind == PendingChangeKind.Move
                || operation.Kind == PendingChangeKind.CreateDirectory)
            {
                nondestructive.Add(operation);
            }
        }

        ordered.AddRange(OrderMoveChains(nondestructive));

        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Update)
            {
                ordered.Add(operation);
            }
        }

        List<JournalOperation> deletes = new List<JournalOperation>();
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Delete
                || operation.Kind == PendingChangeKind.DeleteTree)
            {
                deletes.Add(operation);
            }
        }

        deletes.Sort(static (left, right) => PathDepth(right.Path).CompareTo(PathDepth(left.Path)));
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
    /// <param name="reason">飛ばした理由（成功時は使わない）</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        ThrowIfApplyArmed();
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

        if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Update)
        {
            return TryApplyStagedFile(operation, out reason);
        }

        if (Matches(operation, after: true))
        {
            return TryDeleteStaging(operation.StagingPath, out reason);
        }

        if (!Matches(operation, after: false))
        {
            return false;
        }

        if (operation.Kind == PendingChangeKind.DeleteTree)
        {
            return TryDeleteTree(operation.Path, out reason);
        }

        if (operation.Kind == PendingChangeKind.Delete)
        {
            return operation.IsDirectory
                ? TryDeleteDirectory(operation.Path, out reason)
                : TryDeleteFile(operation.Path, out reason);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            return operation.IsDirectory
                ? TryMoveDirectory(operation.Path, operation.NewPath!, out reason)
                : TryMove(operation.Path, operation.NewPath!, out reason);
        }

        if (operation.Kind == PendingChangeKind.CreateDirectory)
        {
            return true;
        }

        if (string.IsNullOrEmpty(operation.StagingPath) || !File.Exists(operation.StagingPath))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        try
        {
            bool overwrite = operation.Kind == PendingChangeKind.Update;
            File.Move(operation.StagingPath, operation.Path, overwrite);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    /// <summary>
    /// 操作ごとの `.txnew` を消す。無いものは飛ばす
    /// </summary>
    /// <param name="operations">操作一覧</param>
    internal static void DeleteStagingFiles(IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            StagingFile.TryDelete(operation.StagingPath);
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
    internal static void DeleteStagingBackups(string workFolder, Guid transactionId)
    {
        string suffix = "." + transactionId.ToString("D") + ".txnew.prev";
        string pattern = "*" + suffix;
        foreach (string path in Directory.EnumerateFiles(workFolder, pattern, SearchOption.AllDirectories))
        {
            if (WorkPath.IsInMetadataFolder(workFolder, path)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// 作成ディレクトリを深い順に、再帰せず消す
    /// </summary>
    /// <param name="directories">消すディレクトリ</param>
    internal static void DeleteCreatedDirectories(IReadOnlyList<string> directories)
    {
        List<string> pending = new List<string>(directories);
        pending.Sort(static (left, right) =>
        {
            int byDepth = PathDepth(right).CompareTo(PathDepth(left));
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        });

        foreach (string path in pending)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
    }

    /// <summary>
    /// CreateDirectory が作ったディレクトリを、中身ごと消す
    /// </summary>
    /// <param name="operations">操作一覧</param>
    internal static void DeleteCreateDirectoryTrees(IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind != PendingChangeKind.CreateDirectory || !Directory.Exists(operation.Path))
            {
                continue;
            }

            Directory.Delete(operation.Path, recursive: true);
        }
    }

    private static void ThrowIfApplyArmed()
    {
        ApplyFailure? failure = _nextApplyFailure.Value;
        if (failure?.Exception is null)
        {
            return;
        }

        Exception exception = failure.Exception;
        failure.Exception = null;
        throw exception;
    }

    private static bool Matches(JournalOperation operation, bool after)
    {
        PathState? primary = after ? operation.After : operation.Before;
        if (primary is null || !primary.Matches(operation.Path))
        {
            return false;
        }

        if (operation.Kind != PendingChangeKind.Move)
        {
            return true;
        }

        PathState? dest = after ? operation.DestAfter : operation.DestBefore;
        return dest is not null
            && operation.NewPath is not null
            && dest.Matches(operation.NewPath);
    }

    private static bool TryApplyStagedFile(JournalOperation operation, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (string.IsNullOrEmpty(operation.StagingPath))
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }

        // .txnew が残り Before と一致するなら未適用。Before と After が同じ時刻でも適用する
        if (File.Exists(operation.StagingPath) && Matches(operation, after: false))
        {
            try
            {
                bool overwrite = operation.Kind == PendingChangeKind.Update;
                File.Move(operation.StagingPath, operation.Path, overwrite);
                return true;
            }
            catch (IOException exception)
            {
                reason = ClassifyIo(exception);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                reason = OperationFailureReason.IoFailure;
                return false;
            }
        }

        if (Matches(operation, after: true))
        {
            return TryDeleteStaging(operation.StagingPath, out reason);
        }

        return false;
    }

    private static bool TryDeleteStaging(string? stagingPath, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.IoFailure;
        try
        {
            StagingFile.TryDelete(stagingPath);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static bool TryMoveDirectory(string sourcePath, string destPath, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (!Directory.Exists(sourcePath))
        {
            if (File.Exists(sourcePath))
            {
                reason = OperationFailureReason.ReplacedByFile;
                return false;
            }

            return Directory.Exists(destPath);
        }

        if (File.Exists(destPath) || Directory.Exists(destPath))
        {
            reason = OperationFailureReason.AlreadyExists;
            return false;
        }

        try
        {
            Directory.Move(sourcePath, destPath);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static bool TryMove(string sourcePath, string destPath, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (!File.Exists(sourcePath))
        {
            if (Directory.Exists(sourcePath))
            {
                reason = OperationFailureReason.AlreadyExists;
                return false;
            }

            return File.Exists(destPath);
        }

        if (File.Exists(destPath) || Directory.Exists(destPath))
        {
            reason = OperationFailureReason.AlreadyExists;
            return false;
        }

        try
        {
            File.Move(sourcePath, destPath);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
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

    private static int PathDepth(string path)
    {
        int depth = 0;
        foreach (char c in path)
        {
            if (c == System.IO.Path.DirectorySeparatorChar || c == System.IO.Path.AltDirectorySeparatorChar)
            {
                depth++;
            }
        }

        return depth;
    }

    private static bool TryDeleteTree(string path, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (File.Exists(path))
        {
            reason = OperationFailureReason.ReplacedByFile;
            return false;
        }

        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.BeforeAfterMismatch;
        if (File.Exists(path))
        {
            reason = OperationFailureReason.ReplacedByFile;
            return false;
        }

        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static bool TryDeleteFile(string path, out OperationFailureReason reason)
    {
        reason = OperationFailureReason.IoFailure;
        if (Directory.Exists(path))
        {
            reason = OperationFailureReason.AlreadyExists;
            return false;
        }

        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException exception)
        {
            reason = ClassifyIo(exception);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = OperationFailureReason.IoFailure;
            return false;
        }
    }

    private static OperationFailureReason ClassifyIo(IOException exception)
    {
        return PathLockSet.IsSharingViolation(exception)
            ? OperationFailureReason.SharingViolation
            : OperationFailureReason.IoFailure;
    }

    private sealed class ApplyFailure
    {
        internal ApplyFailure(Exception exception)
        {
            Exception = exception;
        }

        internal Exception? Exception { get; set; }
    }
}
