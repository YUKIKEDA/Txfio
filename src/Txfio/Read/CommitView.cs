namespace Txfio;

/// <summary>
/// パスの表からコミット後の姿を解く（ワークフォルダ全体は走査しない）
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
                if (PathMath.SamePath(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind == PendingChangeKind.DeleteTree)
            {
                if (PathMath.IsEqualOrUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                continue;
            }

            if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Update)
            {
                if (PathMath.SamePath(operation.Path, current) && !string.IsNullOrEmpty(operation.StagingPath))
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
                if (PathMath.IsEqualOrUnder(operation.Path, current))
                {
                    return CommitAppearance.Absent();
                }

                if (PathMath.IsEqualOrUnder(operation.NewPath, current))
                {
                    current = PathMath.Rebase(operation.NewPath, operation.Path, current);
                }

                continue;
            }

            if (PathMath.SamePath(operation.Path, current))
            {
                return CommitAppearance.Absent();
            }

            if (PathMath.SamePath(operation.NewPath, current))
            {
                current = operation.Path;
                continue;
            }

            // ファイルで入れ替えた移動先の配下は、コミット後には無い
            if (operation.Overwrite && PathMath.IsUnder(operation.NewPath, current))
            {
                return CommitAppearance.Absent();
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
}
