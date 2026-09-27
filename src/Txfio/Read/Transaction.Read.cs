namespace Txfio;

/// <content>
/// Reads (Read).
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        return ReadCore(path, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        return CommitView.Resolve(_paths.Rows, targetPath).Exists;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string target = IsWorkFolderItself(directoryPath)
            ? _workFolder
            : WorkPath.ResolveInWorkFolder(_workFolder, directoryPath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, target);
        CommitAppearance appearance = CommitView.Resolve(_paths.Rows, target);
        if (!appearance.Exists)
        {
            throw new ExternalConflictException("The directory does not exist: " + target, target);
        }

        if (!appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new UnsupportedOperationException("A file has no entries to list: " + target);
        }

        // Candidates are the direct children on disk and the operations whose parent is this directory; the view is checked one by one.
        string real = appearance.ContentPath;
        HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Directory.EnumerateFileSystemEntries(real))
        {
            string name = System.IO.Path.GetFileName(entry);
            if (!IsThisTransactionSidecar(name))
            {
                candidates.Add(System.IO.Path.Combine(target, name));
            }
        }

        foreach (JournalOperation operation in _paths.Rows)
        {
            AddChildCandidate(candidates, target, real, operation.Path);
            if (operation.NewPath is not null)
            {
                AddChildCandidate(candidates, target, real, operation.NewPath);
            }
        }

        List<DirectoryEntry> entries = new List<DirectoryEntry>();
        foreach (string candidate in candidates)
        {
            if (WorkPath.IsInMetadataFolder(_workFolder, candidate))
            {
                continue;
            }

            CommitAppearance child = CommitView.Resolve(_paths.Rows, candidate);
            if (child.Exists)
            {
                entries.Add(new DirectoryEntry(candidate, child.IsDirectory));
            }
        }

        entries.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    private static void AddChildCandidate(HashSet<string> candidates, string target, string real, string path)
    {
        string? parent = System.IO.Path.GetDirectoryName(path);
        if (string.Equals(parent, target, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(path);
            return;
        }

        // When listing the destination of a directory Move, the operations directly under the source are also candidates, under the destination's name.
        if (string.Equals(parent, real, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(System.IO.Path.Combine(target, System.IO.Path.GetFileName(path)));
        }
    }

    private static FileStream OpenRead(string path, string reportedPath)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // If it is gone when opened, report it as the "target does not exist" contract.
            throw new ExternalConflictException("The file to read does not exist: " + reportedPath, reportedPath);
        }
    }

    private Stream ReadCore(string path, CancellationToken cancellationToken)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        CommitAppearance appearance = CommitView.Resolve(_paths.Rows, targetPath);
        if (!appearance.Exists)
        {
            throw new ExternalConflictException("The file to read does not exist: " + targetPath, targetPath);
        }

        if (appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new UnsupportedOperationException("Reading a directory is not supported: " + targetPath);
        }

        Stream stream = OpenRead(appearance.ContentPath, targetPath);
        _externalChanges?.NoteRead(targetPath, appearance);
        return stream;
    }

    private bool IsWorkFolderItself(string path)
    {
        if (path is "." or "./" or ".\\")
        {
            return true;
        }

        string fullPath = System.IO.Path.IsPathRooted(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(_workFolder, path));
        return string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(fullPath),
            System.IO.Path.TrimEndingDirectorySeparator(_workFolder),
            StringComparison.OrdinalIgnoreCase);
    }

    // This transaction's staging files, restage backups, and swap backups.
    private bool IsThisTransactionSidecar(string name)
    {
        string id = "." + _transactionId.ToString("D") + ".";
        return name.EndsWith(id + "txnew", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(id + "txnew.prev", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(id + "txold", StringComparison.OrdinalIgnoreCase);
    }
}
