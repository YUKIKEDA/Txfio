namespace Txfio;

/// <summary>
/// Compares and rewrites normalized absolute paths (without touching the disk).
/// </summary>
internal static class PathMath
{
    /// <summary>
    /// Returns whether two paths are the same, ignoring case.
    /// </summary>
    /// <param name="left">A path to compare.</param>
    /// <param name="right">The other path to compare.</param>
    /// <returns><see langword="true"/> if they are the same.</returns>
    internal static bool SamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether a path is under a directory (the directory itself is not included).
    /// </summary>
    /// <param name="directoryPath">The directory.</param>
    /// <param name="path">The path to check.</param>
    /// <returns><see langword="true"/> if it is under the directory.</returns>
    internal static bool IsUnder(string directoryPath, string path)
    {
        return path.StartsWith(AsDirectoryPrefix(directoryPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether a path is the directory itself or under it.
    /// </summary>
    /// <param name="directoryPath">The directory.</param>
    /// <param name="path">The path to check.</param>
    /// <returns><see langword="true"/> if it is the directory itself or under it.</returns>
    internal static bool IsEqualOrUnder(string directoryPath, string path)
    {
        return SamePath(directoryPath, path) || IsUnder(directoryPath, path);
    }

    /// <summary>
    /// Moves a path that is the directory <paramref name="from"/> itself or under it to the same position under <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The directory before the move.</param>
    /// <param name="to">The directory after the move.</param>
    /// <param name="path">A path that is <paramref name="from"/> itself or under it.</param>
    /// <returns>The moved path.</returns>
    internal static string Rebase(string from, string to, string path)
    {
        if (SamePath(from, path))
        {
            return to;
        }

        string rest = path.Substring(AsDirectoryPrefix(from).Length);
        return AsDirectoryPrefix(to) + rest;
    }

    /// <summary>
    /// Returns the number of separators (the depth).
    /// </summary>
    /// <param name="path">The path to count.</param>
    /// <returns>The number of separators.</returns>
    internal static int Depth(string path)
    {
        int depth = 0;
        foreach (char character in path)
        {
            if (character == System.IO.Path.DirectorySeparatorChar
                || character == System.IO.Path.AltDirectorySeparatorChar)
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>
    /// Sorts deepest first (at the same depth, by name in reverse order).
    /// </summary>
    /// <remarks>
    /// The order that deletes children before their parents.
    /// </remarks>
    /// <param name="paths">The paths to sort (sorted in place).</param>
    internal static void SortDeepestFirst(List<string> paths)
    {
        paths.Sort(static (left, right) =>
        {
            int byDepth = Depth(right).CompareTo(Depth(left));
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string AsDirectoryPrefix(string directoryPath)
    {
        return directoryPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
    }
}
