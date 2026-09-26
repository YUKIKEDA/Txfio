namespace Txfio.Tests.Stress;

/// <summary>
/// ディレクトリのランダム列の 1 手
/// </summary>
/// <param name="Kind">操作の種類</param>
/// <param name="Path">対象の相対パス</param>
/// <param name="NewPath">Move の移動先。Move 以外は null</param>
/// <param name="Content">Add と Update で書くバイト列。それ以外は null</param>
internal sealed record DirectoryOperation(DirectoryOperationKind Kind, string Path, string? NewPath, byte[]? Content)
{
    /// <summary>
    /// いまの木と、すでに通した手の上で、この手を打てるか
    /// </summary>
    /// <param name="tree">コミット後の姿</param>
    /// <param name="applied">すでに通した手</param>
    /// <returns>打てるとき true</returns>
    public bool CanApply(DirectoryTree tree, IReadOnlyList<DirectoryOperation> applied)
    {
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
                return CanMove(tree, applied);
            default:
                return false;
        }
    }

    /// <summary>
    /// 通った手を木に反映する。Read は何も変えない
    /// </summary>
    /// <param name="tree">コミット後の姿</param>
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
            DirectoryOperationKind.Move => $"Move({Path} -> {NewPath})",
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
