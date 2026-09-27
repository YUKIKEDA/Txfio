namespace Txfio.Tests.Stress;

/// <summary>
/// One text or JSON step.
/// </summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Path">The target relative path.</param>
/// <param name="Text">The string of WriteAllText and AppendAllText. <see langword="null"/> otherwise.</param>
/// <param name="Lines">The lines of line operations. <see langword="null"/> otherwise.</param>
/// <param name="Json">The value of JSON operations. <see langword="null"/> otherwise.</param>
internal sealed record TextOperation(
    TextKind Kind,
    string Path,
    string? Text,
    string[]? Lines,
    StressJsonValue? Json)
{
    /// <summary>
    /// Returns whether this step can be made on the current model.
    /// </summary>
    /// <param name="model">The text after commit.</param>
    /// <returns>true when it can be made.</returns>
    public bool CanApply(TextModel model)
    {
        switch (Kind)
        {
            case TextKind.WriteText:
            case TextKind.WriteLines:
            case TextKind.AppendText:
            case TextKind.AppendLines:
            case TextKind.WriteJson:
                return model.CanWrite(Path);
            case TextKind.ReadText:
            case TextKind.ReadLines:
                return model.IsFile(Path);
            default:
                return Json is not null && model.IsJson(Path, Json);
        }
    }

    /// <summary>
    /// Applies this step to the in-memory text. Reads do nothing.
    /// </summary>
    /// <param name="model">The text after commit.</param>
    public void ApplyTo(TextModel model)
    {
        switch (Kind)
        {
            case TextKind.WriteText:
                model.PutFile(Path, Text ?? string.Empty);
                break;
            case TextKind.WriteLines:
                model.PutFile(Path, TextModel.JoinLines(Lines!));
                break;
            case TextKind.AppendText:
                model.PutFile(Path, Existing(model) + (Text ?? string.Empty));
                break;
            case TextKind.AppendLines:
                model.PutFile(Path, Existing(model) + TextModel.JoinLines(Lines!));
                break;
            case TextKind.WriteJson:
                model.PutFile(Path, System.Text.Json.JsonSerializer.Serialize(Json));
                break;
        }
    }

    /// <summary>
    /// A readable form for failure reports. Content is shown only as its length.
    /// </summary>
    /// <returns>The kind, path, and length.</returns>
    public override string ToString()
    {
        string detail = Kind switch
        {
            TextKind.WriteText or TextKind.AppendText => (Text ?? string.Empty).Length + " characters",
            TextKind.WriteLines or TextKind.AppendLines => (Lines is null ? 0 : Lines.Length) + " lines",
            TextKind.WriteJson or TextKind.ReadJson => "json",
            _ => "read",
        };
        return Kind + "(" + Path + ", " + detail + ")";
    }

    private string Existing(TextModel model)
    {
        return model.IsFile(Path) ? model.Text(Path) : string.Empty;
    }
}
