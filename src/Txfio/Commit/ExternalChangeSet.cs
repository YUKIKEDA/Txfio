namespace Txfio;

/// <summary>
/// The record of sizes and last write times used to compare external changes after staging.
/// </summary>
internal sealed class ExternalChangeSet
{
    private readonly Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _updateToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _foldedOperationToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the real file behind the post-commit view.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="logicalPath">The queried path.</param>
    /// <param name="realPath">The real file that was found.</param>
    /// <returns><see langword="true"/> if a real file exists on disk.</returns>
    internal static bool TryRealFile(
        IReadOnlyList<JournalOperation> operations,
        string logicalPath,
        out string realPath)
    {
        return TryRealFile(CommitView.Resolve(operations, logicalPath), out realPath);
    }

    /// <summary>
    /// Reads the size and last write time of a real file.
    /// </summary>
    /// <param name="realPath">The real file.</param>
    /// <param name="length">The size.</param>
    /// <param name="lastWriteTimeUtc">The last write time (UTC).</param>
    /// <returns><see langword="true"/> if it could be read as a file.</returns>
    internal static bool TryCapture(string realPath, out long length, out DateTime lastWriteTimeUtc)
    {
        length = 0;
        lastWriteTimeUtc = default;
        PathState state = PathState.Capture(realPath);
        if (!state.IsFile || state.Length is null || state.LastWriteTimeUtc is null)
        {
            return false;
        }

        length = state.Length.Value;
        lastWriteTimeUtc = state.LastWriteTimeUtc.Value;
        return true;
    }

    /// <summary>
    /// Updates the record to the current moment each time a real file is read (not when this transaction's staging file (<c>.txnew</c>) is read).
    /// </summary>
    /// <param name="logicalPath">The path that was read.</param>
    /// <param name="appearance">The post-commit view when it was read.</param>
    internal void NoteRead(string logicalPath, CommitAppearance appearance)
    {
        if (appearance.StagedFor is not null)
        {
            if (!TryRealFile(appearance, out string realPath)
                && !TryKnownReal(logicalPath, out realPath))
            {
                return;
            }

            Observe(realPath, replace: false);
            return;
        }

        if (!string.IsNullOrEmpty(appearance.ContentPath))
        {
            Observe(appearance.ContentPath, replace: true);
        }
    }

    /// <summary>
    /// Records the size and last write time of the real file for a file Update.
    /// </summary>
    /// <param name="operations">The list of operations before the Update is added.</param>
    /// <param name="logicalPath">The target path of the Update.</param>
    internal void NoteUpdate(IReadOnlyList<JournalOperation> operations, string logicalPath)
    {
        if (!TryRealFile(operations, logicalPath, out string realPath))
        {
            return;
        }

        if (_snapshots.TryGetValue(realPath, out Snapshot? existing) && existing.FromRead)
        {
            _updateToReal[logicalPath] = realPath;
            return;
        }

        if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
        {
            return;
        }

        _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: false);
        _updateToReal[logicalPath] = realPath;
    }

    /// <summary>
    /// When a file Delete or the source of a file Move is staged, records the real file that will be deleted or moved (uses the read record if there is one).
    /// </summary>
    /// <param name="operations">The list of operations before staging.</param>
    /// <param name="logicalPath">The path to delete, or the Move source.</param>
    internal void NoteRemoval(IReadOnlyList<JournalOperation> operations, string logicalPath)
    {
        if (!TryRealFile(operations, logicalPath, out string realPath))
        {
            return;
        }

        if (!_snapshots.ContainsKey(realPath))
        {
            if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
            {
                return;
            }

            _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: false);
        }

        _removals.Add(realPath);
    }

    /// <summary>
    /// After folding an Update at a Move destination, ties the remaining Add and Delete to the record of the same real file.
    /// </summary>
    /// <param name="updatePath">The Update target before folding (the path of the remaining Add).</param>
    /// <param name="deletePath">The path of the remaining Delete.</param>
    internal void NoteFoldedUpdate(string updatePath, string deletePath)
    {
        if (!_updateToReal.TryGetValue(updatePath, out string? realPath))
        {
            return;
        }

        _updateToReal.Remove(updatePath);
        _foldedOperationToReal[updatePath] = realPath;
        _foldedOperationToReal[deletePath] = realPath;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this operation is a file Update, file Delete, file Move, or what remains after folding, that differs from the record.
    /// </summary>
    /// <param name="operation">The operation being checked.</param>
    /// <returns><see langword="true"/> if the size or last write time differs and the real file is still a file.</returns>
    internal bool IsMismatch(JournalOperation operation)
    {
        if (operation.Kind == PendingChangeKind.Update
            && _updateToReal.TryGetValue(operation.Path, out string? updateReal))
        {
            return Differs(updateReal);
        }

        if (operation.Kind is PendingChangeKind.Add or PendingChangeKind.Delete
            && _foldedOperationToReal.TryGetValue(operation.Path, out string? foldedReal))
        {
            return Differs(foldedReal);
        }

        // For a file Delete and the source of a file Move, the operation's path is the real file that will be deleted or moved.
        if (operation.Kind is PendingChangeKind.Delete or PendingChangeKind.Move
            && !operation.IsDirectory
            && _removals.Contains(operation.Path))
        {
            return Differs(operation.Path);
        }

        return false;
    }

    private static bool TryRealFile(CommitAppearance appearance, out string realPath)
    {
        realPath = string.Empty;
        if (!appearance.Exists || appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            return false;
        }

        // For a staging file, the target path it replaces is the real file.
        string candidate = appearance.StagedFor ?? appearance.ContentPath;
        if (!File.Exists(candidate))
        {
            return false;
        }

        realPath = candidate;
        return true;
    }

    private void Observe(string realPath, bool replace)
    {
        if (!replace && _snapshots.TryGetValue(realPath, out Snapshot? existing))
        {
            existing.FromRead = true;
            return;
        }

        if (!TryCapture(realPath, out long length, out DateTime lastWriteTimeUtc))
        {
            return;
        }

        _snapshots[realPath] = new Snapshot(length, lastWriteTimeUtc, fromRead: true);
    }

    private bool TryKnownReal(string logicalPath, out string realPath)
    {
        if (_updateToReal.TryGetValue(logicalPath, out string? updateReal))
        {
            realPath = updateReal;
            return true;
        }

        if (_foldedOperationToReal.TryGetValue(logicalPath, out string? foldedReal))
        {
            realPath = foldedReal;
            return true;
        }

        realPath = string.Empty;
        return false;
    }

    private bool Differs(string realPath)
    {
        if (!_snapshots.TryGetValue(realPath, out Snapshot? snapshot))
        {
            return false;
        }

        PathState current = PathState.Capture(realPath);
        if (!current.IsFile || current.Length is null || current.LastWriteTimeUtc is null)
        {
            return false;
        }

        return current.Length.Value != snapshot.Length
            || current.LastWriteTimeUtc.Value != snapshot.LastWriteTimeUtc;
    }

    private sealed class Snapshot
    {
        internal Snapshot(long length, DateTime lastWriteTimeUtc, bool fromRead)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
            FromRead = fromRead;
        }

        internal long Length { get; }

        internal DateTime LastWriteTimeUtc { get; }

        internal bool FromRead { get; set; }
    }
}
