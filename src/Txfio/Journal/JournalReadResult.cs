namespace Txfio;

/// <summary>
/// The result of reading a journal (the document that was read, or why it could not be read).
/// </summary>
internal sealed class JournalReadResult
{
    private JournalReadResult(JournalDocument? document, bool unsupportedVersion)
    {
        Document = document;
        UnsupportedVersion = unsupportedVersion;
    }

    /// <summary>
    /// Gets the document that was read (<see langword="null"/> when it could not be read).
    /// </summary>
    internal JournalDocument? Document { get; }

    /// <summary>
    /// Gets a value indicating whether the version differs from this library's version (it may have been left by a newer version of the library, so nothing is touched).
    /// </summary>
    internal bool UnsupportedVersion { get; }

    /// <summary>
    /// Creates a readable result.
    /// </summary>
    /// <param name="document">The document that was read.</param>
    /// <returns>The readable result.</returns>
    internal static JournalReadResult Readable(JournalDocument document)
    {
        return new JournalReadResult(document, unsupportedVersion: false);
    }

    /// <summary>
    /// Creates a result for a corrupt journal that cannot be read.
    /// </summary>
    /// <returns>The corrupt result.</returns>
    internal static JournalReadResult Corrupt()
    {
        return new JournalReadResult(document: null, unsupportedVersion: false);
    }

    /// <summary>
    /// Creates a result for a journal with a different version that cannot be read.
    /// </summary>
    /// <returns>The result for a different version.</returns>
    internal static JournalReadResult OtherVersion()
    {
        return new JournalReadResult(document: null, unsupportedVersion: true);
    }
}
