namespace Txfio.Tests.Stress;

/// <summary>
/// The kinds of operations used in random text and JSON sequences.
/// </summary>
internal enum TextKind
{
    /// <summary>
    /// WriteAllTextAsync.
    /// </summary>
    WriteText,

    /// <summary>
    /// WriteAllLinesAsync.
    /// </summary>
    WriteLines,

    /// <summary>
    /// AppendAllTextAsync.
    /// </summary>
    AppendText,

    /// <summary>
    /// AppendAllLinesAsync.
    /// </summary>
    AppendLines,

    /// <summary>
    /// ReadAllTextAsync.
    /// </summary>
    ReadText,

    /// <summary>
    /// ReadAllLinesAsync.
    /// </summary>
    ReadLines,

    /// <summary>
    /// WriteAsJsonAsync.
    /// </summary>
    WriteJson,

    /// <summary>
    /// ReadFromJsonAsync.
    /// </summary>
    ReadJson,
}
