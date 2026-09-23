namespace Txfio;

/// <content>
/// ワークフォルダの外とのコピー（Import / Export）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task ImportAsync(
        string externalPath,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string external = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalPath);
        string target = WorkPath.ResolveInWorkFolder(_workFolder, targetPath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, target);
        if (Directory.Exists(external))
        {
            throw new UnsupportedOperationException("ディレクトリの取り込みは未対応です: " + external);
        }

        await using FileStream source = OpenExternalFile(external, "取り込み元のファイルが存在しません: " + external, external);
        await StageAsync(PendingChangeKind.Add, target, source, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExportAsync(
        string path,
        string externalPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        string destinationPath = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalPath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        EnsureExportDestination(destinationPath);
        await using FileStream source = OpenExportSource(sourcePath);
        await StagingFile.CopyToNewFileAsync(source, destinationPath, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureExportDestination(string destinationPath)
    {
        if (Directory.Exists(destinationPath))
        {
            throw new ExternalConflictException("コピー先がディレクトリです: " + destinationPath, destinationPath);
        }

        if (File.Exists(destinationPath))
        {
            throw new ExternalConflictException("コピー先のファイルが既に存在します: " + destinationPath, destinationPath);
        }

        StagingRules.EnsureParentDirectoryExists(destinationPath);
    }

    private static FileStream OpenExternalFile(string path, string message, string reportedPath)
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
            throw new ExternalConflictException(message, reportedPath);
        }
    }

    private FileStream OpenExportSource(string sourcePath)
    {
        string? stagingPath = FindStagingPath(sourcePath);
        if (!string.IsNullOrEmpty(stagingPath))
        {
            return OpenExternalFile(stagingPath, "コピー元のファイルが存在しません: " + sourcePath, sourcePath);
        }

        if (Directory.Exists(sourcePath))
        {
            throw new UnsupportedOperationException("ディレクトリのコピーは未対応です: " + sourcePath);
        }

        return OpenExternalFile(sourcePath, "コピー元のファイルが存在しません: " + sourcePath, sourcePath);
    }
}
