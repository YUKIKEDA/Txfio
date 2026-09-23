namespace Txfio;

/// <summary>
/// ステージングした操作を対象パスへ適用する
/// </summary>
internal static class StagingApplier
{
    /// <summary>
    /// Add / Move / Attach を先に、Update を次に、Delete をパスが深い順で後に適用する
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <returns>全て適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApplyAll(IReadOnlyList<JournalOperation> operations)
    {
        bool appliedAll = TryApplyMatching(
            operations,
            static operation => operation.Kind == PendingChangeKind.Add
                || operation.Kind == PendingChangeKind.Move
                || operation.Kind == PendingChangeKind.Attach);
        appliedAll &= TryApplyMatching(
            operations,
            static operation => operation.Kind == PendingChangeKind.Update);

        List<JournalOperation> deletes = new List<JournalOperation>();
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind != PendingChangeKind.Delete)
            {
                continue;
            }

            deletes.Add(operation);
        }

        deletes.Sort(static (left, right) => PathDepth(right.Path).CompareTo(PathDepth(left.Path)));
        foreach (JournalOperation operation in deletes)
        {
            if (!TryApply(operation))
            {
                appliedAll = false;
            }
        }

        return appliedAll;
    }

    /// <summary>
    /// 1 操作を適用する（既に適用済みなら成功、失敗なら <see langword="false"/>）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation)
    {
        if (operation.Kind == PendingChangeKind.Delete)
        {
            return operation.IsDirectory
                ? TryDeleteDirectory(operation.Path)
                : TryDeleteFile(operation.Path);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            if (string.IsNullOrEmpty(operation.NewPath))
            {
                return false;
            }

            return TryMove(operation.Path, operation.NewPath);
        }

        if (operation.Kind == PendingChangeKind.Attach)
        {
            return StagingRules.MatchesExpectedState(
                operation.Path,
                operation.ExpectedLength,
                operation.ExpectedLastWriteTimeUtc);
        }

        if (string.IsNullOrEmpty(operation.StagingPath) || !File.Exists(operation.StagingPath))
        {
            return File.Exists(operation.Path);
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
    }

    /// <summary>
    /// 条件に合う操作だけをジャーナル順で適用する
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <param name="match">適用する操作かどうかを判定する</param>
    /// <returns>該当する操作を全て適用できた、または既に適用済みなら <see langword="true"/></returns>
    private static bool TryApplyMatching(
        IReadOnlyList<JournalOperation> operations,
        Func<JournalOperation, bool> match)
    {
        bool appliedAll = true;
        foreach (JournalOperation operation in operations)
        {
            if (!match(operation))
            {
                continue;
            }

            if (!TryApply(operation))
            {
                appliedAll = false;
            }
        }

        return appliedAll;
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
    }
}
