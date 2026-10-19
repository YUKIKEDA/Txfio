namespace Txfio;

/// <summary>
/// A path overlaps the footprint of an orphaned journal (the paths that recovering it may change); calling <see cref="Txfio.RecoverAsync(string, CancellationToken)"/> resolves it.
/// </summary>
public sealed class RecoveryRequiredException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryRequiredException"/> class with a message and the overlapping path.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="path">The overlapping path, or the work folder when the journal cannot be read.</param>
    public RecoveryRequiredException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the overlapping path, or the work folder when the journal cannot be read (its footprint is then the whole work folder).
    /// </summary>
    public string Path { get; }
}
