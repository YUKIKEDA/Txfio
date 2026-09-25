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
        return Task.FromResult(CommitView.Resolve(_operations, targetPath).Exists);
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
        CommitAppearance appearance = CommitView.Resolve(_operations, targetPath);
        if (!appearance.Exists)
        {
            throw new ExternalConflictException("読み取り対象のファイルが存在しません: " + targetPath, targetPath);
        }

        if (appearance.IsDirectory || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new UnsupportedOperationException("ディレクトリの読み取りは未対応です: " + targetPath);
        }

        return OpenRead(appearance.ContentPath, targetPath);
    }

    private string? FindStagingPath(string targetPath)
    {
        int index = FindOperationIndex(targetPath);
        if (index < 0)
        {
            return null;
        }

        return _operations[index].StagingPath;
    }
}
