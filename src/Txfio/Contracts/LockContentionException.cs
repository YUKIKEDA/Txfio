namespace Txfio;

/// <summary>
/// Another transaction holds the path.
/// </summary>
public sealed class LockContentionException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LockContentionException"/> class with a message and the path that failed.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="path">The path that failed.</param>
    public LockContentionException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the path that failed.
    /// </summary>
    public string Path { get; }
}
