namespace Txfio;

/// <summary>
/// Finds the orphaned journals of a work folder and rejects operations that overlap their footprints (one instance per transaction).
/// </summary>
/// <remarks>
/// An orphaned journal no longer changes: its owner is gone, and <c>RecoverAsync</c> only deletes it.
/// So a footprint, once read, is kept by journal path while the journal is still listed, and its liveness lock is not opened again.
/// Journals that were live at the last check are opened again each time, because a live transaction can crash without its journal changing.
/// </remarks>
internal sealed class OrphanFootprints
{
    private readonly string _workFolder;
    private readonly Dictionary<string, OrphanFootprint> _known = new Dictionary<string, OrphanFootprint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="OrphanFootprints"/> class for a work folder.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    internal OrphanFootprints(string workFolder)
    {
        _workFolder = workFolder;
    }

    /// <summary>
    /// Throws when a path that an operation locks overlaps the footprint of an orphaned journal.
    /// </summary>
    /// <param name="lockedPaths">The normalized absolute paths the operation takes a path lock or an exclusive intent lock on.</param>
    /// <param name="exclusiveIntentPaths">The paths among them whose intent lock is taken exclusively.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when nothing overlaps.</returns>
    /// <exception cref="RecoveryRequiredException">A path overlaps (its <c>Path</c> is the first overlapping path in lexical order of the upper-cased absolute paths, or the work folder for an unreadable journal).</exception>
    /// <exception cref="IOException">Listing the journals, opening a liveness lock, or reading a journal failed for a reason other than a sharing violation.</exception>
    internal async Task ThrowIfOverlapAsync(
        IEnumerable<string> lockedPaths,
        IEnumerable<string> exclusiveIntentPaths,
        CancellationToken cancellationToken)
    {
        HashSet<string> exclusive = new HashSet<string>(exclusiveIntentPaths, StringComparer.OrdinalIgnoreCase);
        string[] ordered = lockedPaths
            .Concat(exclusive)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.ToUpperInvariant(), StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        IReadOnlyList<OrphanFootprint> footprints = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (footprints.Any(static footprint => footprint.WholeWorkFolder))
        {
            throw Required(_workFolder);
        }

        foreach (string path in ordered)
        {
            bool exclusiveIntent = exclusive.Contains(path);
            if (footprints.Any(footprint => footprint.Overlaps(path, exclusiveIntent)))
            {
                throw Required(path);
            }
        }
    }

    private static RecoveryRequiredException Required(string path)
    {
        return new RecoveryRequiredException(
            "A crashed transaction that touched this path has not been recovered. Call RecoverAsync first: " + path,
            path);
    }

    private async Task<IReadOnlyList<OrphanFootprint>> CurrentAsync(CancellationToken cancellationToken)
    {
        string metadataFolder = MetadataNames.FolderPath(_workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            _known.Clear();
            return Array.Empty<OrphanFootprint>();
        }

        string[] journals = Directory.GetFiles(
            metadataFolder,
            MetadataNames.JournalSearchPattern,
            SearchOption.TopDirectoryOnly);
        HashSet<string> listed = new HashSet<string>(journals, StringComparer.OrdinalIgnoreCase);
        foreach (string gone in _known.Keys.Where(path => !listed.Contains(path)).ToArray())
        {
            _known.Remove(gone);
        }

        List<OrphanFootprint> footprints = new List<OrphanFootprint>();
        foreach (string journalPath in journals)
        {
            if (_known.TryGetValue(journalPath, out OrphanFootprint? known))
            {
                footprints.Add(known);
                continue;
            }

            // A sharing violation means the owner is alive (our own journal always gives one, since we hold its lock).
            using (FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath)))
            {
                if (liveness is null)
                {
                    continue;
                }
            }

            // Read after closing the liveness lock, so other checks and RecoverAsync see it held only for a moment.
            JournalReadResult? read = await JournalStore.ReadOrphanAsync(journalPath, cancellationToken).ConfigureAwait(false);
            if (read is null)
            {
                continue;
            }

            if (read.Document is null)
            {
                // Read again at each check, because someone may fix it.
                footprints.Add(OrphanFootprint.Unknown);
                continue;
            }

            OrphanFootprint footprint = OrphanFootprint.From(read.Document);
            _known.Add(journalPath, footprint);
            footprints.Add(footprint);
        }

        return footprints;
    }
}
