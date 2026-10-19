namespace Txfio;

/// <summary>
/// The paths that recovering one orphaned journal may change (see "The footprint of an orphaned journal" in the design).
/// </summary>
internal sealed class OrphanFootprint
{
    private readonly string[] _entries;

    private OrphanFootprint(string[] entries, bool wholeWorkFolder)
    {
        _entries = entries;
        WholeWorkFolder = wholeWorkFolder;
    }

    /// <summary>
    /// Gets the footprint of an unreadable journal, which covers the whole work folder.
    /// </summary>
    internal static OrphanFootprint Unknown { get; } = new OrphanFootprint(Array.Empty<string>(), wholeWorkFolder: true);

    /// <summary>
    /// Gets a value indicating whether the footprint is the whole work folder (the journal could not be read).
    /// </summary>
    internal bool WholeWorkFolder { get; }

    /// <summary>
    /// Gets the entries (normalized absolute paths). Each entry covers itself and everything under it.
    /// </summary>
    internal IReadOnlyList<string> Entries => _entries;

    /// <summary>
    /// Builds the footprint of a readable journal.
    /// </summary>
    /// <remarks>
    /// Without <c>Committing</c>, rollback deletes only staging files named after the orphan's transaction ID, so only the directories it deletes can hold another transaction's work.
    /// With <c>Committing</c>, roll-forward compares directories by existence only, so every path an operation names is an entry.
    /// </remarks>
    /// <param name="document">The journal document with absolute paths.</param>
    /// <returns>The footprint.</returns>
    internal static OrphanFootprint From(JournalDocument document)
    {
        List<string> entries = new List<string>();
        foreach (JournalOperation operation in document.Operations)
        {
            if (document.Committing)
            {
                entries.Add(operation.Path);
                if (operation.NewPath is not null)
                {
                    entries.Add(operation.NewPath);
                }
            }
            else if (operation.Kind == PendingChangeKind.CreateDirectory)
            {
                entries.Add(operation.Path);
            }
        }

        entries.AddRange(document.CreatedDirectories);
        return new OrphanFootprint(entries.ToArray(), wholeWorkFolder: false);
    }

    /// <summary>
    /// Returns whether a path that an operation locks overlaps this footprint.
    /// </summary>
    /// <param name="path">The normalized absolute path the operation locks.</param>
    /// <param name="exclusiveIntent"><see langword="true"/> when the operation takes the intent lock of <paramref name="path"/> exclusively.</param>
    /// <returns><see langword="true"/> when the path is an entry or under one, or is an ancestor of an entry and its intent lock is taken exclusively.</returns>
    internal bool Overlaps(string path, bool exclusiveIntent)
    {
        if (WholeWorkFolder)
        {
            return true;
        }

        foreach (string entry in _entries)
        {
            if (PathMath.IsEqualOrUnder(entry, path))
            {
                return true;
            }

            if (exclusiveIntent && PathMath.IsUnder(path, entry))
            {
                return true;
            }
        }

        return false;
    }
}
