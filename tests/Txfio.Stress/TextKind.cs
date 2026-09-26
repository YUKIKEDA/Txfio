namespace Txfio.Tests.Stress;

/// <summary>
/// 文字列と JSON のランダム列で使う操作の種類
/// </summary>
internal enum TextKind
{
    /// <summary>
    /// WriteAllTextAsync
    /// </summary>
    WriteText,

    /// <summary>
    /// WriteAllLinesAsync
    /// </summary>
    WriteLines,

    /// <summary>
    /// AppendAllTextAsync
    /// </summary>
    AppendText,

    /// <summary>
    /// AppendAllLinesAsync
    /// </summary>
    AppendLines,

    /// <summary>
    /// ReadAllTextAsync
    /// </summary>
    ReadText,

    /// <summary>
    /// ReadAllLinesAsync
    /// </summary>
    ReadLines,

    /// <summary>
    /// WriteAsJsonAsync
    /// </summary>
    WriteJson,

    /// <summary>
    /// ReadFromJsonAsync
    /// </summary>
    ReadJson,
}
