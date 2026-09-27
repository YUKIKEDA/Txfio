namespace Txfio;

/// <summary>
/// The post-commit view of a queried path.
/// </summary>
internal readonly struct CommitAppearance
{
    private CommitAppearance(bool exists, bool isDirectory, string? contentPath, string? stagedFor)
    {
        Exists = exists;
        IsDirectory = isDirectory;
        ContentPath = contentPath;
        StagedFor = stagedFor;
    }

    /// <summary>
    /// Gets a value indicating whether a file or directory exists.
    /// </summary>
    internal bool Exists { get; }

    /// <summary>
    /// Gets a value indicating whether it is a directory.
    /// </summary>
    internal bool IsDirectory { get; }

    /// <summary>
    /// Gets the path of what to read (<see langword="null"/> when missing).
    /// </summary>
    internal string? ContentPath { get; }

    /// <summary>
    /// Gets the target path of the operation that wrote the staging file, when the content is a staging file (<see langword="null"/> when reading the real file).
    /// </summary>
    internal string? StagedFor { get; }

    /// <summary>
    /// Returns a missing view.
    /// </summary>
    /// <returns>A missing view.</returns>
    internal static CommitAppearance Absent()
    {
        return new CommitAppearance(exists: false, isDirectory: false, contentPath: null, stagedFor: null);
    }

    /// <summary>
    /// Returns the view of a file that reads the real file as is.
    /// </summary>
    /// <param name="contentPath">The path of the real file to read.</param>
    /// <returns>A view with a file.</returns>
    internal static CommitAppearance File(string contentPath)
    {
        return new CommitAppearance(exists: true, isDirectory: false, contentPath, stagedFor: null);
    }

    /// <summary>
    /// Returns the view of a file that reads a staging file.
    /// </summary>
    /// <param name="operation">The Add or Update that decided the content.</param>
    /// <returns>A view with a file.</returns>
    internal static CommitAppearance Staged(JournalOperation operation)
    {
        return new CommitAppearance(exists: true, isDirectory: false, operation.StagingPath, operation.Path);
    }

    /// <summary>
    /// Returns the view of a directory.
    /// </summary>
    /// <param name="contentPath">The path of the real directory.</param>
    /// <returns>A view with a directory.</returns>
    internal static CommitAppearance Directory(string contentPath)
    {
        return new CommitAppearance(exists: true, isDirectory: true, contentPath, stagedFor: null);
    }
}
