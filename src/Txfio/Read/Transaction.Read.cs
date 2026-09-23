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
            if (!File.Exists(stagingPath))
            {
                throw new ExternalConflictException("読み取り対象のファイルが存在しません: " + targetPath, targetPath);
            }

            return Task.FromResult<Stream>(OpenRead(stagingPath));
        }

        if (Directory.Exists(targetPath))
        {
            throw new UnsupportedOperationException("ディレクトリの読み取りは未対応です: " + targetPath);
        }

        if (!File.Exists(targetPath))
        {
            throw new ExternalConflictException("読み取り対象のファイルが存在しません: " + targetPath, targetPath);
        }

        return Task.FromResult<Stream>(OpenRead(targetPath));
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);
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
