using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// Whether a path exists, and for a file, its size and last write time.
/// </summary>
internal sealed class PathState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PathState"/> class with existence, and for a file, the size and last write time.
    /// </summary>
    /// <param name="exists"><see langword="true"/> if the path exists.</param>
    /// <param name="length">The file size (<see langword="null"/> for a directory or a missing path).</param>
    /// <param name="lastWriteTimeUtc">The last write time of the file (UTC; <see langword="null"/> for a directory or a missing path).</param>
    [JsonConstructor]
    public PathState(bool exists, long? length = null, DateTime? lastWriteTimeUtc = null)
    {
        Exists = exists;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>
    /// Gets a value indicating whether the path exists.
    /// </summary>
    public bool Exists { get; }

    /// <summary>
    /// Gets the file size (<see langword="null"/> for a directory or a missing path).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Length { get; }

    /// <summary>
    /// Gets the last write time of the file (UTC; <see langword="null"/> for a directory or a missing path).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastWriteTimeUtc { get; }

    /// <summary>
    /// Gets the state of a missing path.
    /// </summary>
    internal static PathState Absent { get; } = new PathState(exists: false);

    /// <summary>
    /// Gets a value indicating whether it exists as a directory.
    /// </summary>
    internal bool IsDirectory => Exists && Length is null && LastWriteTimeUtc is null;

    /// <summary>
    /// Gets a value indicating whether it exists as a file.
    /// </summary>
    internal bool IsFile => Exists && Length is not null && LastWriteTimeUtc is not null;

    /// <summary>
    /// Reads a path on disk as a file, a directory, or missing.
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <returns>The state of that path.</returns>
    internal static PathState Capture(string path)
    {
        if (File.Exists(path))
        {
            FileInfo info = new FileInfo(path);
            return new PathState(exists: true, info.Length, info.LastWriteTimeUtc);
        }

        if (Directory.Exists(path))
        {
            return new PathState(exists: true);
        }

        return Absent;
    }

    /// <summary>
    /// Returns whether the path on disk matches this state exactly.
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <returns><see langword="true"/> if it matches.</returns>
    internal bool Matches(string path)
    {
        if (File.Exists(path))
        {
            if (Length is null || LastWriteTimeUtc is null)
            {
                return false;
            }

            FileInfo info = new FileInfo(path);
            return info.Length == Length.Value && info.LastWriteTimeUtc == LastWriteTimeUtc.Value;
        }

        if (Directory.Exists(path))
        {
            return IsDirectory;
        }

        return !Exists;
    }

    /// <summary>
    /// Returns whether another state has the same existence, size, and last write time.
    /// </summary>
    /// <param name="other">The state to compare.</param>
    /// <returns><see langword="true"/> if they are the same.</returns>
    internal bool SameAs(PathState other)
    {
        return Exists == other.Exists
            && Length == other.Length
            && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }
}
