namespace Txfio;

/// <content>
/// 読み取り（Read）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        return Task.FromResult(ReadCore(path, cancellationToken));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        return Task.FromResult(CommitView.Resolve(_paths.Rows, targetPath).Exists);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
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
            throw new ExternalConflictException("ディレクトリが存在しません: " + target, target);
        }

        if (!appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new UnsupportedOperationException("ファイルの直下は一覧できません: " + target);
        }

        // 候補はディスク上の直下と、親がこのディレクトリである操作であり、姿は 1 件ずつ確かめる
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
        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(entries);
    }

    private static void AddChildCandidate(HashSet<string> candidates, string target, string real, string path)
    {
        string? parent = System.IO.Path.GetDirectoryName(path);
        if (string.Equals(parent, target, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(path);
            return;
        }

        // ディレクトリ Move の移動先を一覧するとき、移動元の直下の操作も移動先の名前で候補に入れる
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
            // 開く時点で無ければ、対象が無い契約として返す
            throw new ExternalConflictException("読み取り対象のファイルが存在しません: " + reportedPath, reportedPath);
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
            throw new ExternalConflictException("読み取り対象のファイルが存在しません: " + targetPath, targetPath);
        }

        if (appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new UnsupportedOperationException("ディレクトリの読み取りは未対応です: " + targetPath);
        }

        Stream stream = OpenRead(appearance.ContentPath, targetPath);
        _externalChanges?.NoteRead(_paths.Rows, targetPath, appearance.ContentPath, _transactionId);
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

    // このトランザクションのステージングファイル、再ステージの退避、入れ替えの退避
    private bool IsThisTransactionSidecar(string name)
    {
        string id = "." + _transactionId.ToString("D") + ".";
        return name.EndsWith(id + "txnew", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(id + "txnew.prev", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(id + "txold", StringComparison.OrdinalIgnoreCase);
    }

    private string? FindStagingPath(string targetPath)
    {
        int index = FindOperationIndex(targetPath);
        if (index < 0)
        {
            return null;
        }

        return _paths.Rows[index].StagingPath;
    }
}
