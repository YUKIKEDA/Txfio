namespace Txfio;

/// <summary>
/// One operation in the journal.
/// </summary>
public sealed class PendingChange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PendingChange"/> class with the operation kind and the target path.
    /// </summary>
    /// <param name="kind">The operation kind.</param>
    /// <param name="path">The target path.</param>
    /// <param name="newPath">The Move destination (<see langword="null"/> otherwise).</param>
    public PendingChange(PendingChangeKind kind, string path, string? newPath = null)
    {
        Kind = kind;
        Path = path;
        NewPath = newPath;
    }

    /// <summary>
    /// Gets the operation kind.
    /// </summary>
    public PendingChangeKind Kind { get; }

    /// <summary>
    /// Gets the target path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the Move destination path.
    /// </summary>
    public string? NewPath { get; }
}
