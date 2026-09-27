namespace Txfio.Tests.Stress;

/// <summary>
/// One step of a random directory sequence.
/// </summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Path">The target relative path.</param>
/// <param name="NewPath">The Move destination. <see langword="null"/> except for Move.</param>
/// <param name="Content">The bytes written by Add and Update. <see langword="null"/> otherwise.</param>
/// <param name="Overwrite">true when a Move replaces its destination.</param>
internal sealed record DirectoryOperation(
    DirectoryOperationKind Kind,
    string Path,
    string? NewPath,
    byte[]? Content,
    bool Overwrite = false)
{
    /// <summary>
    /// Returns whether this step can be made on the current tree and the steps already passed.
    /// </summary>
    /// <param name="tree">The post-commit view.</param>
    /// <param name="applied">The steps already passed.</param>
    /// <returns>true when it can be made.</returns>
    public bool CanApply(DirectoryTree tree, IReadOnlyList<DirectoryOperation> applied)
    {
        if (IsFrozen(applied, Path) || (NewPath is not null && IsFrozen(applied, NewPath)))
        {
            return false;
        }

        switch (Kind)
        {
            case DirectoryOperationKind.CreateDirectory:
                return !tree.Contains(Path) && tree.IsDirectory(DirectoryTree.Parent(Path));
            case DirectoryOperationKind.Add:
                return !tree.Contains(Path) && tree.IsDirectory(DirectoryTree.Parent(Path));
            case DirectoryOperationKind.Update:
            case DirectoryOperationKind.Read:
                return tree.IsFile(Path);
            case DirectoryOperationKind.Delete:
                return tree.IsFile(Path) || (tree.IsDirectory(Path) && tree.IsEmpty(Path) && !Touches(applied, Path));
            case DirectoryOperationKind.DeleteTree:
                return tree.IsDirectory(Path) && !Touches(applied, Path);
            case DirectoryOperationKind.Move:
                return Overwrite ? CanOverwrite(tree, applied) : CanMove(tree, applied);
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies a passed step to the tree. Read changes nothing.
    /// </summary>
    /// <param name="tree">The post-commit view.</param>
    public void ApplyTo(DirectoryTree tree)
    {
        switch (Kind)
        {
            case DirectoryOperationKind.CreateDirectory:
                tree.AddDirectory(Path);
                break;
            case DirectoryOperationKind.Add:
            case DirectoryOperationKind.Update:
                tree.PutFile(Path, Content!);
                break;
            case DirectoryOperationKind.Delete:
                tree.Remove(Path);
                break;
            case DirectoryOperationKind.DeleteTree:
                tree.RemoveTree(Path);
                break;
            case DirectoryOperationKind.Move:
                if (Overwrite && tree.Contains(NewPath!))
                {
                    if (tree.IsDirectory(NewPath!))
                    {
                        tree.RemoveTree(NewPath!);
                    }
                    else
                    {
                        tree.Remove(NewPath!);
                    }
                }

                tree.Move(Path, NewPath!);
                break;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            DirectoryOperationKind.Add or DirectoryOperationKind.Update => $"{Kind}({Path}, {StressContent.Describe(Content!)})",
            DirectoryOperationKind.Move => Overwrite
                ? $"Move({Path} -> {NewPath}, overwrite)"
                : $"Move({Path} -> {NewPath})",
            _ => $"{Kind}({Path})",
        };
    }

    private bool CanMove(DirectoryTree tree, IReadOnlyList<DirectoryOperation> applied)
    {
        if (NewPath is null || !tree.Contains(Path) || tree.Contains(NewPath))
        {
            return false;
        }

        if (!tree.IsDirectory(DirectoryTree.Parent(NewPath)))
        {
            return false;
        }

        if (Path == NewPath || DirectoryTree.IsUnder(NewPath, Path) || DirectoryTree.IsUnder(Path, NewPath))
        {
            return false;
        }

        return !tree.IsDirectory(Path) || !Touches(applied, Path);
    }

    private bool CanOverwrite(DirectoryTree tree, IReadOnlyList<DirectoryOperation> applied)
    {
        if (NewPath is null || Path == NewPath || !tree.Contains(Path) || !tree.Contains(NewPath))
        {
            return false;
        }

        if (DirectoryTree.IsUnder(NewPath, Path) || DirectoryTree.IsUnder(Path, NewPath))
        {
            return false;
        }

        return !Touches(applied, Path) && !Touches(applied, NewPath);
    }

    private static bool IsFrozen(IReadOnlyList<DirectoryOperation> applied, string path)
    {
        foreach (DirectoryOperation operation in applied)
        {
            if (!operation.Overwrite || operation.NewPath is null)
            {
                continue;
            }

            if (path == operation.Path
                || path == operation.NewPath
                || DirectoryTree.IsUnder(path, operation.Path)
                || DirectoryTree.IsUnder(path, operation.NewPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Touches(IReadOnlyList<DirectoryOperation> applied, string directory)
    {
        foreach (DirectoryOperation operation in applied)
        {
            if (Hits(operation.Path, directory) || (operation.NewPath is not null && Hits(operation.NewPath, directory)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Hits(string path, string directory)
    {
        return path == directory || DirectoryTree.IsUnder(path, directory);
    }
}
