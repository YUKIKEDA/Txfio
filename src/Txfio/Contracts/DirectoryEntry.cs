namespace Txfio;

/// <summary>
/// One entry directly under a directory in the post-commit view.
/// </summary>
public sealed class DirectoryEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryEntry"/> class with a path and a kind.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="isDirectory"><see langword="true"/> for a directory, <see langword="false"/> for a file.</param>
    public DirectoryEntry(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
    }

    /// <summary>
    /// Gets the absolute path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets a value indicating whether the entry is a directory (<see langword="true"/>) or a file (<see langword="false"/>).
    /// </summary>
    public bool IsDirectory { get; }
}
