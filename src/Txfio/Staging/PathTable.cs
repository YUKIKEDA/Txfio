namespace Txfio;

/// <summary>
/// The table of rows that represent path operations in a transaction.
/// </summary>
internal sealed class PathTable
{
    private readonly List<JournalOperation> _rows = new List<JournalOperation>();
    private readonly Dictionary<string, int> _firstByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _moveDestination = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the rows of the table (in journal order; change them only through the table methods, to keep positions aligned).
    /// </summary>
    internal IReadOnlyList<JournalOperation> Rows => _rows;

    /// <summary>
    /// Gets the number of rows.
    /// </summary>
    internal int Count => _rows.Count;

    /// <summary>
    /// Gets the row at the given position.
    /// </summary>
    /// <param name="index">The zero-based position.</param>
    internal JournalOperation this[int index] => _rows[index];

    /// <summary>
    /// Removes a file Move and folds it into a row that deletes the source.
    /// </summary>
    /// <remarks>
    /// If the source was added again, that is the same as deleting and writing, so that Add becomes an Update.
    /// If another Move is going to come into the source, the apply order (Move first, Delete after) cannot express it, so it is not accepted.
    /// </remarks>
    /// <param name="operations">The operations to fold (changed in place).</param>
    /// <param name="moveIndex">The position of the file Move to remove.</param>
    /// <param name="destination">The operation to add in place of the Move (if <see langword="null"/>, the source's Delete is placed at the Move's position).</param>
    internal static void FoldMoveOutToSourceDelete(
        List<JournalOperation> operations,
        int moveIndex,
        JournalOperation? destination)
    {
        string sourcePath = operations[moveIndex].Path;
        operations.RemoveAt(moveIndex);
        if (destination is not null)
        {
            operations.Add(destination);
        }

        int readdedIndex = operations.FindIndex(
            operation => string.Equals(operation.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (readdedIndex >= 0)
        {
            JournalOperation readded = operations[readdedIndex];
            if (readded.Kind != PendingChangeKind.Add)
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }

            operations[readdedIndex] = new JournalOperation(PendingChangeKind.Update, sourcePath, readded.StagingPath);
            return;
        }

        bool movedInto = operations.Exists(
            operation => operation.Kind == PendingChangeKind.Move
                && string.Equals(operation.NewPath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (movedInto)
        {
            throw new InvalidOperationException("Another file is scheduled to move into the source, so this cannot fold into deleting the original: " + sourcePath);
        }

        JournalOperation delete = new JournalOperation(PendingChangeKind.Delete, sourcePath);
        if (destination is null)
        {
            operations.Insert(moveIndex, delete);
        }
        else
        {
            operations.Add(delete);
        }
    }

    /// <summary>
    /// Returns the rows as an array for the journal.
    /// </summary>
    /// <returns>An array of the rows of the table.</returns>
    internal JournalOperation[] ToArray()
    {
        return _rows.ToArray();
    }

    /// <summary>
    /// Adds a row to the end.
    /// </summary>
    /// <param name="operation">The operation to add.</param>
    internal void Add(JournalOperation operation)
    {
        _rows.Add(operation);
        Note(operation, _rows.Count - 1);
    }

    /// <summary>
    /// Removes the row that is the same instance.
    /// </summary>
    /// <param name="operation">The operation to remove.</param>
    internal void Remove(JournalOperation operation)
    {
        _rows.Remove(operation);
        Reindex();
    }

    /// <summary>
    /// Removes the row at the given position.
    /// </summary>
    /// <param name="index">The position to remove.</param>
    internal void RemoveAt(int index)
    {
        _rows.RemoveAt(index);
        Reindex();
    }

    /// <summary>
    /// Inserts a row at the given position.
    /// </summary>
    /// <param name="index">The position to insert at.</param>
    /// <param name="operation">The operation to insert.</param>
    internal void Insert(int index, JournalOperation operation)
    {
        _rows.Insert(index, operation);
        Reindex();
    }

    /// <summary>
    /// Replaces the row at the given position.
    /// </summary>
    /// <param name="index">The position to replace.</param>
    /// <param name="operation">The new operation.</param>
    internal void Set(int index, JournalOperation operation)
    {
        _rows[index] = operation;
        Reindex();
    }

    /// <summary>
    /// Returns the position of the row that is the same instance.
    /// </summary>
    /// <param name="operation">The operation to find.</param>
    /// <returns>-1 if not found.</returns>
    internal int IndexOf(JournalOperation operation)
    {
        return _rows.IndexOf(operation);
    }

    /// <summary>
    /// Removes all rows.
    /// </summary>
    internal void Clear()
    {
        _rows.Clear();
        _firstByPath.Clear();
        _moveDestination.Clear();
    }

    /// <summary>
    /// Replaces all rows.
    /// </summary>
    /// <param name="operations">The new rows.</param>
    internal void Load(IEnumerable<JournalOperation> operations)
    {
        _rows.Clear();
        _rows.AddRange(operations);
        Reindex();
    }

    /// <summary>
    /// Returns the position of the first row for the path.
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <returns>-1 if not found.</returns>
    internal int FindOperationIndex(string path)
    {
        return _firstByPath.TryGetValue(path, out int index) ? index : -1;
    }

    /// <summary>
    /// Finds a row for the same path after the given position.
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <param name="afterIndex">Search after this position.</param>
    /// <returns>-1 if not found.</returns>
    internal int FindLaterOperationIndex(string path, int afterIndex)
    {
        for (int i = afterIndex + 1; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the position of the Move whose destination is this path.
    /// </summary>
    /// <param name="destPath">The destination.</param>
    /// <returns>-1 if not found.</returns>
    internal int FindMoveToIndex(string destPath)
    {
        return _moveDestination.TryGetValue(destPath, out int index) ? index : -1;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the row is the source of a file Move.
    /// </summary>
    /// <param name="index">The position of the row.</param>
    /// <returns><see langword="true"/> for a file Move.</returns>
    internal bool IsFileMoveOut(int index)
    {
        return index >= 0
            && _rows[index].Kind == PendingChangeKind.Move
            && !_rows[index].IsDirectory;
    }

    /// <summary>
    /// For the source of a file Move, returns the position of the row that decides the later content.
    /// </summary>
    /// <param name="path">The source.</param>
    /// <param name="moveOutIndex">The position of the file Move.</param>
    /// <returns>The position of a row rewritten to the source, or of a Move coming in from elsewhere (-1 if none).</returns>
    internal int FindContentAfterMoveOut(string path, int moveOutIndex)
    {
        int later = FindLaterOperationIndex(path, moveOutIndex);
        return later >= 0 ? later : FindMoveToIndex(path);
    }

    /// <summary>
    /// Decides which row of the table a file Add or Update becomes.
    /// </summary>
    /// <param name="targetPath">The target path.</param>
    /// <param name="kind">The kind the call requested.</param>
    /// <param name="existingIndex">The position of the row to replace (-1 to add).</param>
    /// <param name="moveToIndex">The position of the Move to fold as an Update at its destination (-1 if none).</param>
    /// <param name="addOntoFileMove"><see langword="true"/> when adding to the source of a file Move.</param>
    /// <param name="recordedKind">The kind to keep in the table.</param>
    internal void PlanStageFile(
        string targetPath,
        PendingChangeKind kind,
        out int existingIndex,
        out int moveToIndex,
        out bool addOntoFileMove,
        out PendingChangeKind recordedKind)
    {
        existingIndex = FindOperationIndex(targetPath);
        recordedKind = kind;
        moveToIndex = -1;
        addOntoFileMove = false;
        if (existingIndex >= 0)
        {
            if (_rows[existingIndex].IsDirectory)
            {
                throw AlreadyStaged();
            }

            if (_rows[existingIndex].Kind == PendingChangeKind.Move)
            {
                int contentIndex = FindLaterOperationIndex(targetPath, existingIndex);
                if (contentIndex >= 0)
                {
                    existingIndex = contentIndex;
                    recordedKind = RestageKind(_rows[existingIndex].Kind, kind);
                }
                else if (FindMoveToIndex(targetPath) >= 0)
                {
                    // Another Move brings a file in, so an Update is the same as an Update at that Move's destination.
                    if (kind != PendingChangeKind.Update)
                    {
                        throw AlreadyStaged();
                    }

                    moveToIndex = FindMoveToIndex(targetPath);
                    existingIndex = -1;
                }
                else if (kind == PendingChangeKind.Add)
                {
                    addOntoFileMove = true;
                    existingIndex = -1;
                }
                else
                {
                    throw AlreadyStaged();
                }
            }
            else
            {
                recordedKind = RestageKind(_rows[existingIndex].Kind, kind);
            }

            return;
        }

        moveToIndex = FindMoveToIndex(targetPath);
        if (moveToIndex >= 0
            && (kind != PendingChangeKind.Update || _rows[moveToIndex].IsDirectory))
        {
            throw AlreadyStaged();
        }
    }

    /// <summary>
    /// Rejects a Move that would close a cycle with no free end.
    /// </summary>
    /// <param name="replacement">The Move to put in.</param>
    /// <param name="replaceIndex">The position to replace (-1 to add).</param>
    internal void ThrowIfMoveChainCloses(JournalOperation replacement, int replaceIndex)
    {
        List<JournalOperation> prospective = new List<JournalOperation>(_rows);
        if (replaceIndex >= 0)
        {
            prospective[replaceIndex] = replacement;
        }
        else
        {
            prospective.Add(replacement);
        }

        if (!StagingApplier.MovesReachFreeEnd(prospective))
        {
            throw new InvalidOperationException("A move without a free end is not accepted");
        }
    }

    /// <summary>
    /// Decides the kind to keep in the table when a file is rewritten at the same path.
    /// </summary>
    /// <param name="existingKind">The kind already recorded.</param>
    /// <param name="requestedKind">The kind of this operation.</param>
    /// <returns>The kind to keep in the table.</returns>
    private static PendingChangeKind RestageKind(PendingChangeKind existingKind, PendingChangeKind requestedKind)
    {
        if (existingKind == requestedKind)
        {
            return existingKind;
        }

        if (existingKind == PendingChangeKind.Add && requestedKind == PendingChangeKind.Update)
        {
            return PendingChangeKind.Add;
        }

        if (existingKind == PendingChangeKind.Delete
            && (requestedKind == PendingChangeKind.Add || requestedKind == PendingChangeKind.Update))
        {
            return PendingChangeKind.Update;
        }

        throw AlreadyStaged();
    }

    private static InvalidOperationException AlreadyStaged()
    {
        return new InvalidOperationException("This path is already staged by another operation");
    }

    private void Note(JournalOperation operation, int index)
    {
        if (!_firstByPath.ContainsKey(operation.Path))
        {
            _firstByPath[operation.Path] = index;
        }

        if (operation.Kind == PendingChangeKind.Move
            && !string.IsNullOrEmpty(operation.NewPath)
            && !_moveDestination.ContainsKey(operation.NewPath))
        {
            _moveDestination[operation.NewPath] = index;
        }
    }

    private void Reindex()
    {
        _firstByPath.Clear();
        _moveDestination.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            Note(_rows[i], i);
        }
    }
}
