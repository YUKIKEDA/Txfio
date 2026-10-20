using System.Text.RegularExpressions;

namespace Txfio;

/// <summary>
/// Recognizes the names of staging entries (<c>{name}.{txid}.txnew</c>, <c>{name}.{txid}.txnew.prev</c>, and <c>{name}.{txid}.txold</c>) of any transaction.
/// </summary>
internal static class StagingNames
{
    private static readonly Regex _pattern = new Regex(
        @"^.+\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(?:txnew|txnew\.prev|txold)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// Returns whether a single name is a staging name, after trimming trailing dots and spaces.
    /// </summary>
    /// <param name="name">One file or directory name (not a path).</param>
    /// <returns><see langword="true"/> if the name is a staging name.</returns>
    internal static bool IsMatch(string name)
    {
        return _pattern.IsMatch(name.TrimEnd('.', ' '));
    }

    /// <summary>
    /// Returns the first segment of a path below a root that is a staging name.
    /// </summary>
    /// <param name="root">The root whose own segments are not checked.</param>
    /// <param name="fullPath">A normalized absolute path under <paramref name="root"/>.</param>
    /// <returns>The path up to and including the first staging-name segment, or <see langword="null"/> if there is none.</returns>
    internal static string? FindReservedSegment(string root, string fullPath)
    {
        string relative = System.IO.Path.GetRelativePath(root, fullPath);
        string[] segments = relative.Split(
            new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        string current = System.IO.Path.TrimEndingDirectorySeparator(root);
        foreach (string segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
            if (IsMatch(segment))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Throws when a path below the work folder has a segment that is a staging name.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPath">A normalized absolute path inside the work folder.</param>
    /// <exception cref="InvalidOperationException">A segment is a staging name.</exception>
    internal static void ThrowIfReserved(string workFolder, string fullPath)
    {
        string? reserved = FindReservedSegment(workFolder, fullPath);
        if (reserved is not null)
        {
            throw new InvalidOperationException("A path with a staging name cannot be used: " + reserved);
        }
    }
}
