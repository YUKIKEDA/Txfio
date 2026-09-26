namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列の 1 手
/// </summary>
/// <param name="Kind">操作の種類</param>
/// <param name="Path">対象の相対パス</param>
/// <param name="NewPath">Move の移動先。Move 以外は null</param>
/// <param name="Content">Add と Update で書くバイト列。それ以外は null</param>
internal sealed record RandomOperation(RandomOperationKind Kind, string Path, string? NewPath, byte[]? Content)
{
    /// <summary>
    /// モデルの上でこの手を打てるか。Add は無いパス、それ以外はあるパスが対象
    /// </summary>
    /// <param name="model">相対パスから内容への辞書</param>
    /// <returns>打てるとき true</returns>
    public bool CanApply(IReadOnlyDictionary<string, byte[]> model)
    {
        return Kind switch
        {
            RandomOperationKind.Add => !model.ContainsKey(Path),
            RandomOperationKind.Move => model.ContainsKey(Path) && !model.ContainsKey(NewPath!),
            _ => model.ContainsKey(Path),
        };
    }

    /// <summary>
    /// モデルにこの手を反映する。Read は何も変えない
    /// </summary>
    /// <param name="model">相対パスから内容への辞書</param>
    public void ApplyTo(Dictionary<string, byte[]> model)
    {
        switch (Kind)
        {
            case RandomOperationKind.Add:
            case RandomOperationKind.Update:
                model[Path] = Content!;
                break;
            case RandomOperationKind.Delete:
                model.Remove(Path);
                break;
            case RandomOperationKind.Move:
                model[NewPath!] = model[Path];
                model.Remove(Path);
                break;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            RandomOperationKind.Add or RandomOperationKind.Update => $"{Kind}({Path}, {StressContent.Describe(Content!)})",
            RandomOperationKind.Move => $"Move({Path} -> {NewPath})",
            _ => $"{Kind}({Path})",
        };
    }
}
