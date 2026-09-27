using System.IO.Compression;

namespace Txfio;

/// <summary>
/// Checks ZIP entry names before extracting, and turns them into paths relative to the destination.
/// </summary>
internal static class ArchiveEntryNames
{
    private static readonly char[] _invalidCharacters = CreateInvalidCharacters();

    private static readonly HashSet<string> _reservedNames = CreateReservedNames();

    /// <summary>
    /// Checks every entry name, and returns the extract plan.
    /// </summary>
    /// <param name="entries">The ZIP entries.</param>
    /// <returns>The relative path and kind of each entry.</returns>
    /// <exception cref="InvalidDataException">There is a dangerous name, a name not valid on Windows, a duplicate, or a file and a directory with the same name.</exception>
    internal static IReadOnlyList<ArchiveEntryPlan> Plan(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        List<ArchiveEntryPlan> plans = new List<ArchiveEntryPlan>(entries.Count);
        NameSet names = new NameSet();
        foreach (ZipArchiveEntry entry in entries)
        {
            string[] segments = names.Add(entry.FullName, out bool isDirectory);
            plans.Add(new ArchiveEntryPlan(entry, System.IO.Path.Combine(segments), isDirectory));
        }

        names.EnsureNoFileDirectoryConflict();
        return plans;
    }

    /// <summary>
    /// Checks every entry name that is about to be written.
    /// </summary>
    /// <param name="fullNames">The entry names (directories end with <c>/</c>).</param>
    /// <exception cref="InvalidDataException">There is a dangerous name, a name not valid on Windows, a duplicate, or a file and a directory with the same name.</exception>
    internal static void Validate(IEnumerable<string> fullNames)
    {
        NameSet names = new NameSet();
        foreach (string fullName in fullNames)
        {
            names.Add(fullName, out _);
        }

        names.EnsureNoFileDirectoryConflict();
    }

    /// <summary>
    /// Splits an entry name, and rejects names that cannot be used.
    /// </summary>
    /// <param name="fullName">The ZIP entry name.</param>
    /// <param name="isDirectory">Returns <see langword="true"/> for a directory entry that ends with a separator.</param>
    /// <returns>The split name.</returns>
    /// <exception cref="InvalidDataException">The name leaves the destination, or is not valid on Windows.</exception>
    internal static string[] Split(string fullName, out bool isDirectory)
    {
        string normalized = fullName.Replace('\\', '/');
        isDirectory = normalized.EndsWith('/');
        string trimmed = isDirectory ? normalized[..^1] : normalized;
        string[] segments = trimmed.Split('/');
        foreach (string segment in segments)
        {
            EnsureValidSegment(segment, fullName);
        }

        return segments;
    }

    private static void EnsureValidSegment(string segment, string fullName)
    {
        if (segment.Length == 0 || segment == "." || segment == "..")
        {
            throw new InvalidDataException("The entry name leaves the destination: " + fullName);
        }

        if (segment.IndexOfAny(_invalidCharacters) >= 0
            || segment.EndsWith('.')
            || segment.EndsWith(' ')
            || _reservedNames.Contains(segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant()))
        {
            throw new InvalidDataException("The entry name is not valid in a Windows path: " + fullName);
        }

        if (segment.EndsWith(".txnew", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An entry name ending in .txnew cannot be extracted: " + fullName);
        }
    }

    private static char[] CreateInvalidCharacters()
    {
        List<char> characters = new List<char> { '<', '>', ':', '"', '|', '?', '*' };
        for (char control = '\0'; control < ' '; control++)
        {
            characters.Add(control);
        }

        return characters.ToArray();
    }

    private static HashSet<string> CreateReservedNames()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal) { "CON", "PRN", "AUX", "NUL" };
        for (int number = 1; number <= 9; number++)
        {
            names.Add("COM" + number);
            names.Add("LPT" + number);
        }

        return names;
    }

    private sealed class NameSet
    {
        private readonly HashSet<string> _files = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _ancestors = new HashSet<string>(StringComparer.Ordinal);

        internal string[] Add(string fullName, out bool isDirectory)
        {
            string[] segments = Split(fullName, out isDirectory);
            string key = string.Join('/', segments).ToUpperInvariant();
            if (_files.Contains(key) || _directories.Contains(key))
            {
                throw new InvalidDataException("The ZIP has entries with the same name: " + fullName);
            }

            (isDirectory ? _directories : _files).Add(key);
            for (int count = 1; count < segments.Length; count++)
            {
                _ancestors.Add(string.Join('/', segments, 0, count).ToUpperInvariant());
            }

            return segments;
        }

        internal void EnsureNoFileDirectoryConflict()
        {
            foreach (string file in _files)
            {
                if (_ancestors.Contains(file))
                {
                    throw new InvalidDataException("The ZIP has a file and a directory with the same name: " + file);
                }
            }
        }
    }
}
