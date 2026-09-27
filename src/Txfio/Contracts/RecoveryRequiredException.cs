namespace Txfio;

/// <summary>
/// An orphaned journal remains; calling <see cref="Txfio.RecoverAsync(string, CancellationToken)"/> resolves it.
/// </summary>
public sealed class RecoveryRequiredException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryRequiredException"/> class with a message and the work folder.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="path">The work folder.</param>
    public RecoveryRequiredException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the work folder.
    /// </summary>
    public string Path { get; }
}
