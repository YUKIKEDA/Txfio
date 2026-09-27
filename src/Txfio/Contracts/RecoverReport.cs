namespace Txfio;

/// <summary>
/// The result of <see cref="Txfio.RecoverAsync(string, CancellationToken)"/>.
/// </summary>
public sealed class RecoverReport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecoverReport"/> class with the overall result and the journals processed.
    /// </summary>
    /// <param name="result">The overall result, by priority.</param>
    /// <param name="journals">The journals processed (live journals are not included; in lexical order of the path, ignoring case).</param>
    public RecoverReport(RecoverResult result, IReadOnlyList<JournalReport> journals)
    {
        ArgumentNullException.ThrowIfNull(journals);
        Result = result;
        Journals = journals;
    }

    /// <summary>
    /// Gets the overall result, by priority.
    /// </summary>
    public RecoverResult Result { get; }

    /// <summary>
    /// Gets the journals processed (live journals are not included; in lexical order of the path, ignoring case).
    /// </summary>
    public IReadOnlyList<JournalReport> Journals { get; }
}
