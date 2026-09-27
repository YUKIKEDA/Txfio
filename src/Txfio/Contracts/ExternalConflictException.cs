namespace Txfio;

/// <summary>
/// A precondition on disk no longer holds.
/// </summary>
public sealed class ExternalConflictException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalConflictException"/> class with a message and the path that failed.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="path">The path that failed.</param>
    public ExternalConflictException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the path that failed.
    /// </summary>
    public string Path { get; }
}
