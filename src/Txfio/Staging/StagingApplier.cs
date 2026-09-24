namespace Txfio;

/// <summary>
/// ステージングした操作を対象パスへ適用する
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
    /// Add / Move / CreateDirectory を先に、Update を次に、Delete と DeleteTree をパスが深い順で後に適用する
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <returns>全て適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApplyAll(IReadOnlyList<JournalOperation> operations)
    {
        bool appliedAll = true;
        foreach (JournalOperation operation in InApplyOrder(operations))
        {
            if (!TryApply(operation))
            {
                appliedAll = false;
                continue;
            }

            CrashInjector.CheckPoint(CrashInjector.AfterApply);
        }

        return appliedAll;
    }

    /// <summary>
    /// Add / Move / CreateDirectory、Update、Delete と DeleteTree（深い順）の順に並べる
    /// </summary>
    /// <param name="operations">操作一覧</param>
    /// <returns>適用順の操作</returns>
    internal static JournalOperation[] InApplyOrder(IReadOnlyList<JournalOperation> operations)
    {
        List<JournalOperation> ordered = new List<JournalOperation>(operations.Count);
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Add
                || operation.Kind == PendingChangeKind.Move
                || operation.Kind == PendingChangeKind.CreateDirectory)
            {
                ordered.Add(operation);
            }
        }

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
    /// 1 操作を適用する（既に適用済みなら成功、失敗なら <see langword="false"/>）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation)
    {
        ThrowIfApplyArmed();
        if (operation.Before is null || operation.After is null)
        {
            return false;
        }

        if (operation.Kind == PendingChangeKind.Move
            && (string.IsNullOrEmpty(operation.NewPath)
                || operation.DestBefore is null
                || operation.DestAfter is null))
        {
            return false;
        }

        if (Matches(operation, after: true))
        {
            return TryDeleteStaging(operation.StagingPath);
        }

        if (!Matches(operation, after: false))
        {
            return false;
        }

        if (operation.Kind == PendingChangeKind.DeleteTree)
        {
            return TryDeleteTree(operation.Path);
        }

        if (operation.Kind == PendingChangeKind.Delete)
        {
            return operation.IsDirectory
                ? TryDeleteDirectory(operation.Path)
                : TryDeleteFile(operation.Path);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            return operation.IsDirectory
                ? TryMoveDirectory(operation.Path, operation.NewPath!)
                : TryMove(operation.Path, operation.NewPath!);
        }

        if (operation.Kind == PendingChangeKind.CreateDirectory)
        {
            return true;
        }

        if (string.IsNullOrEmpty(operation.StagingPath) || !File.Exists(operation.StagingPath))
        {
            return false;
        }

        try
        {
            bool overwrite = operation.Kind == PendingChangeKind.Update;
            File.Move(operation.StagingPath, operation.Path, overwrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
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

    private static bool TryDeleteStaging(string? stagingPath)
    {
        try
        {
            StagingFile.TryDelete(stagingPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMoveDirectory(string sourcePath, string destPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return Directory.Exists(destPath);
        }

        if (File.Exists(destPath) || Directory.Exists(destPath))
        {
            return false;
        }

        try
        {
            Directory.Move(sourcePath, destPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMove(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
        {
            return File.Exists(destPath);
        }

        if (File.Exists(destPath))
        {
            return false;
        }

        try
        {
            File.Move(sourcePath, destPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

    private static bool TryDeleteTree(string path)
    {
        if (File.Exists(path))
        {
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
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (File.Exists(path))
        {
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
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
