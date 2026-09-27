namespace Txfio.Tests.Stress;

/// <summary>
/// One step of a random sequence.
/// </summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Path">The target relative path.</param>
/// <param name="NewPath">The Move destination. <see langword="null"/> except for Move.</param>
/// <param name="Content">The bytes written by Add and Update. <see langword="null"/> otherwise.</param>
internal sealed record RandomOperation(RandomOperationKind Kind, string Path, string? NewPath, byte[]? Content)
{
    /// <summary>
    /// Returns whether this step can be made on the model. Add targets a missing path; the others target an existing path.
    /// </summary>
    /// <param name="model">A dictionary from relative path to content.</param>
    /// <returns>true when it can be made.</returns>
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
    /// Applies this step to the model. Read changes nothing.
    /// </summary>
    /// <param name="model">A dictionary from relative path to content.</param>
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
