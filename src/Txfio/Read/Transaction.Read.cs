namespace Txfio;

/// <content>
/// 読み取り（Read）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureNotMetadataFolder(_workFolder, targetPath);
        string? stagingPath = FindStagingPath(targetPath);
        if (!string.IsNullOrEmpty(stagingPath))
        {
            return Task.FromResult<Stream>(OpenRead(stagingPath, targetPath));
        }

        if (Directory.Exists(targetPath))
        {
            throw new UnsupportedOperationException("ディレクトリの読み取りは未対応です: " + targetPath);
        }

        return Task.FromResult<Stream>(OpenRead(targetPath, targetPath));
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
