namespace Txfio;

/// <summary>
/// 操作一覧をコミット後の姿へ投影する（ワークフォルダ全体は走査しない）
/// </summary>
internal static class CommitView
{
    /// <summary>
    /// 問い合わせたパスのコミット後の姿を返す
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="targetPath">正規化済みの絶対パス</param>
    /// <returns>コミット後の姿</returns>
    internal static CommitAppearance Resolve(IReadOnlyList<JournalOperation> operations, string targetPath)
    {
        JournalOperation[] ordered = StagingApplier.InApplyOrder(operations);
        string current = targetPath;
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            JournalOperation operation = ordered[i];
            if (operation.Kind == PendingChangeKind.Delete)
            {
                if (SamePath(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind == PendingChangeKind.DeleteTree)
            {
                if (SamePath(operation.Path, current) || IsUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Update)
            {
                if (SamePath(operation.Path, current) && !string.IsNullOrEmpty(operation.StagingPath))
                {
                    return CommitAppearance.File(operation.StagingPath);
                }

                continue;
            }

            if (operation.Kind != PendingChangeKind.Move || string.IsNullOrEmpty(operation.NewPath))
            {
                continue;
            }

            if (operation.IsDirectory)
            {
                if (SamePath(operation.Path, current) || IsUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                if (SamePath(operation.NewPath, current) || IsUnder(operation.NewPath, current))
                {
                    current = Rewrite(operation.NewPath, operation.Path, current);
                }

                continue;
            }

            if (SamePath(operation.Path, current))
            {
                return CommitAppearance.Absent();
            }

            if (SamePath(operation.NewPath, current))
            {
                current = operation.Path;
            }
        }

        if (File.Exists(current))
        {
            return CommitAppearance.File(current);
        }

        if (Directory.Exists(current))
        {
            return CommitAppearance.Directory(current);
        }

        return CommitAppearance.Absent();
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string directoryPath, string path)
    {
        string prefix = directoryPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rewrite(string destination, string source, string current)
    {
        if (SamePath(destination, current))
        {
            return source;
        }

        string prefix = destination.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        string rest = current.Substring(prefix.Length);
        string root = source.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        return root + System.IO.Path.DirectorySeparatorChar + rest;
    }
}
