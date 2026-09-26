namespace Txfio.Tests.Stress;

/// <summary>
/// 文字列と JSON の 1 手
/// </summary>
/// <param name="Kind">操作の種類</param>
/// <param name="Path">対象の相対パス</param>
/// <param name="Text">WriteAllText と AppendAllText の文字列。それ以外は null</param>
/// <param name="Lines">行の操作の行。それ以外は null</param>
/// <param name="Json">JSON の操作の値。それ以外は null</param>
internal sealed record TextOperation(
    TextKind Kind,
    string Path,
    string? Text,
    string[]? Lines,
    StressJsonValue? Json)
{
    /// <summary>
    /// いまのモデルでこの手を打てるか
    /// </summary>
    /// <param name="model">コミット後のテキスト</param>
    /// <returns>打てるとき true</returns>
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
    /// メモリ上のテキストへこの手を反映する。読み取りは何もしない
    /// </summary>
    /// <param name="model">コミット後のテキスト</param>
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
    /// 失敗の報告に使う、読める形。中身は長さだけ出す
    /// </summary>
    /// <returns>種類とパスと長さ</returns>
    public override string ToString()
    {
        string detail = Kind switch
        {
            TextKind.WriteText or TextKind.AppendText => (Text ?? string.Empty).Length + " 文字",
            TextKind.WriteLines or TextKind.AppendLines => (Lines is null ? 0 : Lines.Length) + " 行",
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
