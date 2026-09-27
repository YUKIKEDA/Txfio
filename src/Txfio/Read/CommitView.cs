namespace Txfio;

/// <summary>
/// Resolves the post-commit view from the path table (the whole work folder is not walked).
/// </summary>
internal static class CommitView
{
    /// <summary>
    /// Returns the post-commit view of the queried path.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="targetPath">The normalized absolute path.</param>
    /// <returns>The post-commit view.</returns>
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
                    return CommitAppearance.Staged(operation);
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

            // Nothing exists under a destination that was swapped for a file after commit.
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
