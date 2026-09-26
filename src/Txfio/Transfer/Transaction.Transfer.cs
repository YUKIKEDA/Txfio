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
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string external = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalPath);
        string target = WorkPath.ResolveInWorkFolder(_workFolder, targetPath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, target);
        if (Directory.Exists(external))
        {
            await ImportDirectoryAsync(external, target, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (File.Exists(external) && IsReparsePoint(external))
        {
            throw new InvalidOperationException("シンボリックリンクはコピーできません: " + external);
        }

        await using FileStream source = OpenExternalFile(external, "コピー元のファイルが存在しません: " + external, external);
        await StageAsync(PendingChangeKind.Add, target, source, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExportAsync(
        string path,
        string externalPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        string destinationPath = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalPath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, sourcePath);
        CommitAppearance appearance = CommitView.Resolve(_paths.Rows, sourcePath);
        if (appearance.IsDirectory)
        {
            await ExportDirectoryAsync(appearance.ContentPath ?? sourcePath, destinationPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!appearance.Exists || string.IsNullOrEmpty(appearance.ContentPath))
        {
            throw new ExternalConflictException("コピー元のファイルが存在しません: " + sourcePath, sourcePath);
        }

        if (IsReparsePoint(appearance.ContentPath))
        {
            throw new InvalidOperationException("シンボリックリンクはコピーできません: " + sourcePath);
        }

        EnsureExportDestination(destinationPath);
        await using FileStream source = OpenExternalFile(
            appearance.ContentPath,
            "コピー元のファイルが存在しません: " + sourcePath,
            sourcePath);
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

    private static void CreateExportDirectory(string path, List<string> createdDirectories)
    {
        Directory.CreateDirectory(path);
        createdDirectories.Add(path);
    }

    private static void DeleteExportedFiles(List<string> createdFiles)
    {
        foreach (string path in createdFiles)
        {
            StagingFile.TryDelete(path);
        }
    }

    private static void DeleteExportedDirectories(List<string> createdDirectories)
    {
        createdDirectories.Sort(static (left, right) =>
        {
            int byDepth = DirectoryDepth(right).CompareTo(DirectoryDepth(left));
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        });

        foreach (string path in createdDirectories)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
            }
            catch (IOException)
            {
                // 失敗したコピーの後始末では、元の例外を残す
            }
            catch (UnauthorizedAccessException)
            {
                // 失敗したコピーの後始末では、元の例外を残す
            }
        }
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

    private async Task ImportDirectoryAsync(
        string external,
        string target,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        StagingRules.ThrowIfCopyDestinationInsideSource(external, target);
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, target);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, target);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, target);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, external);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, target);
        ThrowIfCopyPathIsStaged(target);
        EnsureCopyDestinationFree(target);
        await _locks.AcquireSharedAsync(_workFolder).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { target }, target).ConfigureAwait(false);
        await using PathLockSet.WorkFolderExclusive exclusive = await _locks.EnterExclusiveAsync(_workFolder).ConfigureAwait(false);
        if (!Directory.Exists(external))
        {
            throw new ExternalConflictException("コピー元のディレクトリが存在しません: " + external, external);
        }

        EnsureCopyDestinationFree(target);
        List<string> directories = new List<string> { target };
        List<PlannedTreeFile> files = new List<PlannedTreeFile>();
        if (!IsReparsePoint(external))
        {
            PlanCopiedTree(external, external, target, directories, files, cancellationToken);
        }

        await ApplyCopiedTreeAsync(directories, files, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExportDirectoryAsync(
        string sourcePath,
        string destinationPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureExportDestination(destinationPath);
        List<string> createdDirectories = new List<string>();
        List<string> createdFiles = new List<string>();
        try
        {
            if (!Directory.Exists(sourcePath))
            {
                throw new ExternalConflictException("コピー元のディレクトリが存在しません: " + sourcePath, sourcePath);
            }

            EnsureExportDestination(destinationPath);
            CreateExportDirectory(destinationPath, createdDirectories);
            DirectoryCopyProgress tracker = new DirectoryCopyProgress(progress);
            if (!IsReparsePoint(sourcePath))
            {
                await ExportDirectoryEntriesAsync(
                        sourcePath,
                        sourcePath,
                        destinationPath,
                        tracker,
                        createdDirectories,
                        createdFiles,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!tracker.Reported)
            {
                progress?.Report(new TransferProgress(0, null));
            }
        }
        catch
        {
            DeleteExportedFiles(createdFiles);
            DeleteExportedDirectories(createdDirectories);
            throw;
        }
    }

    private async Task ExportDirectoryEntriesAsync(
        string sourceRoot,
        string current,
        string destinationRoot,
        DirectoryCopyProgress progress,
        List<string> createdDirectories,
        List<string> createdFiles,
        CancellationToken cancellationToken)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SkipIntakeEntry(entry))
            {
                continue;
            }

            string relative = System.IO.Path.GetRelativePath(sourceRoot, entry);
            string destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(destinationRoot, relative));
            if (Directory.Exists(entry))
            {
                CreateExportDirectory(destination, createdDirectories);
                await ExportDirectoryEntriesAsync(
                        sourceRoot,
                        entry,
                        destinationRoot,
                        progress,
                        createdDirectories,
                        createdFiles,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!File.Exists(entry))
            {
                continue;
            }

            await using FileStream source = OpenExportSource(entry);
            await StagingFile.CopyToNewFileAsync(source, destination, progress, cancellationToken)
                .ConfigureAwait(false);
            createdFiles.Add(destination);
            progress.CompleteFile(new FileInfo(destination).Length);
        }
    }

    private FileStream OpenExportSource(string sourcePath)
    {
        string? stagingPath = FindStagingPath(sourcePath);
        if (!string.IsNullOrEmpty(stagingPath))
        {
            return OpenExternalFile(stagingPath, "コピー元のファイルが存在しません: " + sourcePath, sourcePath);
        }

        return OpenExternalFile(sourcePath, "コピー元のファイルが存在しません: " + sourcePath, sourcePath);
    }
}
