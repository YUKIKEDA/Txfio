namespace Txfio;

/// <summary>
/// ステージングした操作を対象パスへ適用する
/// </summary>
internal static class StagingApplier
{
    /// <summary>
    /// Add / Update / Move を先に、Delete を後に適用する
    /// </summary>
    /// <param name="operations">適用する操作一覧</param>
    /// <returns>全て適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApplyAll(IReadOnlyList<JournalOperation> operations)
    {
        bool appliedAll = true;
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Delete)
            {
                continue;
            }

            if (!TryApply(operation))
            {
                appliedAll = false;
            }
        }

        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind != PendingChangeKind.Delete)
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

    /// <summary>
    /// 1 操作を適用する（既に適用済みなら成功、失敗なら <see langword="false"/>）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation)
    {
        if (operation.Kind == PendingChangeKind.Delete)
        {
            return TryDelete(operation.Path);
        }

        if (operation.Kind == PendingChangeKind.Move)
        {
            if (string.IsNullOrEmpty(operation.NewPath))
            {
                return false;
            }

            return TryMove(operation.Path, operation.NewPath);
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

    private static bool TryDelete(string path)
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
