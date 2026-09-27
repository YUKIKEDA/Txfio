namespace Txfio.Tests.Support;

/// <summary>
/// Records whether the journal contains a given name at the moment progress arrives (called synchronously).
/// </summary>
internal sealed class JournalProbeProgress : IProgress<TransferProgress>
{
    private readonly string _workFolder;
    private readonly string _expected;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalProbeProgress"/> class with the work folder to check and the name the journal should contain.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="expected">The file name the journal should contain.</param>
    internal JournalProbeProgress(string workFolder, string expected)
    {
        _workFolder = workFolder;
        _expected = expected;
    }

    /// <summary>
    /// Gets a value indicating whether progress arrived at least once.
    /// </summary>
    internal bool Reported { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the journal contained the name every time progress arrived.
    /// </summary>
    internal bool AlwaysJournaled { get; private set; } = true;

    /// <inheritdoc />
    public void Report(TransferProgress value)
    {
        Reported = true;
        string metadata = System.IO.Path.Combine(_workFolder, ".txfio");
        string journal = Directory.GetFiles(metadata, "tx-*.journal").Single();
        string text = File.ReadAllText(journal);
        if (!text.Contains(_expected, StringComparison.OrdinalIgnoreCase))
        {
            AlwaysJournaled = false;
        }
    }
}
