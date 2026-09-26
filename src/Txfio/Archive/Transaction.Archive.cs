using System.IO.Compression;

namespace Txfio;

/// <content>
/// ZIP アーカイブの作成（ワークフォルダ内への Add と、外への書き出し）
/// </content>
internal sealed partial class Transaction
{
    private static readonly DateTime _minimumEntryTime = new DateTime(1980, 1, 1, 0, 0, 0);
    private static readonly DateTime _maximumEntryTime = new DateTime(2107, 12, 31, 23, 59, 58);

    /// <inheritdoc />
    public async Task CreateArchiveAsync(
        string source,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, source);
        ArchiveRoot root = ToSingleArchiveRoot(sourcePath, includeBaseDirectory);
        await CreateArchiveCoreAsync(
            new[] { root },
            archivePath,
            compressionLevel,
            validateNames: false,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ArchiveRoot> roots = ToArchiveRoots(entries);
        await CreateArchiveCoreAsync(
            roots,
            archivePath,
            compressionLevel,
            validateNames: true,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExportArchiveAsync(
        string source,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, source);
        ArchiveRoot root = ToSingleArchiveRoot(sourcePath, includeBaseDirectory);
        await ExportArchiveCoreAsync(
            new[] { root },
            externalArchivePath,
            compressionLevel,
            validateNames: false,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExportArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ArchiveRoot> roots = ToArchiveRoots(entries);
        await ExportArchiveCoreAsync(
            roots,
            externalArchivePath,
            compressionLevel,
            validateNames: true,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static ArchiveRoot ToSingleArchiveRoot(string sourcePath, bool includeBaseDirectory)
    {
        string name = System.IO.Path.GetFileName(sourcePath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        if (Directory.Exists(sourcePath))
        {
            return new ArchiveRoot(sourcePath, true, includeBaseDirectory ? name : string.Empty);
        }

        return new ArchiveRoot(sourcePath, false, name);
    }

    private static string ToArchiveRootName(string entryName, bool isDirectory)
    {
        string name = entryName.Replace('\\', '/');
        try
        {
            if (!isDirectory)
            {
                ArchiveEntryNames.Split(name, out bool endsWithSeparator);
                if (endsWithSeparator)
                {
                    throw new InvalidDataException("ファイルのエントリ名が区切りで終わっています: " + entryName);
                }

                return name;
            }

            if (name.Length == 0)
            {
                return name;
            }

            string trimmed = name.EndsWith('/') ? name[..^1] : name;
            ArchiveEntryNames.Split(trimmed + "/", out _);
            return trimmed;
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, "entries", exception);
        }
    }

    private static void ValidateArchiveNames(List<PlannedArchiveEntry> planned)
    {
        try
        {
            ArchiveEntryNames.Validate(planned.Select(static entry => entry.Name));
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, "entries", exception);
        }
    }

    private static FileStream OpenNewExportArchive(string destinationPath)
    {
        try
        {
            return new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (IOException exception) when (exception is DirectoryNotFoundException || File.Exists(destinationPath))
        {
            if (exception is DirectoryNotFoundException)
            {
                string parent = System.IO.Path.GetDirectoryName(destinationPath) ?? destinationPath;
                throw new ExternalConflictException("親ディレクトリが存在しません: " + parent, parent);
            }

            throw new ExternalConflictException("ZIP のパスに既にファイルがあります: " + destinationPath, destinationPath);
        }
    }

    private static FileStream OpenArchiveSourceFromDisk(string path)
    {
        return OpenExternalFile(path, "入力のファイルが存在しません: " + path, path);
    }

    private static DateTimeOffset ToEntryTime(DateTime lastWriteTime)
    {
        if (lastWriteTime < _minimumEntryTime)
        {
            return _minimumEntryTime;
        }

        if (lastWriteTime > _maximumEntryTime)
        {
            return _maximumEntryTime;
        }

        return lastWriteTime;
    }

    private static async Task WriteEntryAsync(
        ZipArchive zip,
        string entryName,
        FileStream content,
        CompressionLevel compressionLevel,
        DirectoryCopyProgress progress,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, compressionLevel);
        entry.LastWriteTime = ToEntryTime(File.GetLastWriteTime(content.Name));
        long copied;
        Stream entryStream = entry.Open();
        await using (entryStream.ConfigureAwait(false))
        {
            await StagingFile.CopyAsync(content, entryStream, null, progress, cancellationToken)
                .ConfigureAwait(false);
            copied = content.Position;
        }

        progress.CompleteFile(copied);
    }

    private static async Task WriteArchiveAsync(
        Stream output,
        List<PlannedArchiveEntry> planned,
        CompressionLevel compressionLevel,
        Func<string, FileStream> openFile,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        DirectoryCopyProgress tracker = new DirectoryCopyProgress(progress);
        using (ZipArchive zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (PlannedArchiveEntry entry in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FilePath is null)
                {
                    zip.CreateEntry(entry.Name);
                    continue;
                }

                await using FileStream content = openFile(entry.FilePath);
                await WriteEntryAsync(zip, entry.Name, content, compressionLevel, tracker, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!tracker.Reported)
        {
            progress?.Report(new TransferProgress(0, null));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<ArchiveRoot> ToArchiveRoots(IEnumerable<ArchiveEntrySource> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<ArchiveRoot> roots = new List<ArchiveRoot>();
        foreach (ArchiveEntrySource? entry in entries.ToList())
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entries), "ZIP に入れる要素が null です");
            }

            if (entry.SourcePath is null)
            {
                throw new ArgumentNullException(nameof(entries), "ZIP に入れる要素のパスが null です");
            }

            string sourcePath = WorkPath.ResolveInWorkFolder(_workFolder, entry.SourcePath);
            bool isDirectory = Directory.Exists(sourcePath);
            string entryName = entry.EntryName
                ?? System.IO.Path.GetRelativePath(_workFolder, sourcePath);
            roots.Add(new ArchiveRoot(sourcePath, isDirectory, ToArchiveRootName(entryName, isDirectory)));
        }

        return roots;
    }

    private async Task CreateArchiveCoreAsync(
        IReadOnlyList<ArchiveRoot> roots,
        string archivePath,
        CompressionLevel compressionLevel,
        bool validateNames,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string archive = WorkPath.ResolveInWorkFolder(_workFolder, archivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, archive);
        foreach (ArchiveRoot root in roots)
        {
            StagingRules.EnsureNotMetadataFolder(_workFolder, root.SourcePath);
            StagingRules.ThrowIfArchiveInsideSource(root.SourcePath, archive);
            StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, root.SourcePath);
            StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, root.SourcePath);
            StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, root.SourcePath);
            StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, root.SourcePath);
            ThrowIfCopyPathIsStaged(root.SourcePath);
        }

        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, archive);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, archive);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, archive);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, archive);
        ThrowIfCopyPathIsStaged(archive);
        foreach (ArchiveRoot root in roots)
        {
            EnsureCopySourceAvailable(root.SourcePath);
        }

        EnsureCopyDestinationFree(archive);
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        using PathLockSet.ReadingScope reading = await _locks.AcquireForReadingAsync(
                _workFolder,
                roots.Select(static root => root.SourcePath).Append(archive).ToArray(),
                Array.Empty<string>(),
                roots.Where(static root => root.IsDirectory).Select(static root => root.SourcePath).ToArray(),
                _lockAttempt)
            .ConfigureAwait(false);
        foreach (ArchiveRoot root in roots)
        {
            if (root.IsDirectory ? !Directory.Exists(root.SourcePath) : !File.Exists(root.SourcePath))
            {
                throw new ExternalConflictException("入力が存在しません: " + root.SourcePath, root.SourcePath);
            }
        }

        EnsureCopyDestinationFree(archive);
        List<PlannedArchiveEntry> planned = PlanArchiveEntries(roots, cancellationToken);
        if (validateNames)
        {
            ValidateArchiveNames(planned);
        }

        string stagingPath = WorkPath.StagingFilePath(archive, _transactionId);
        int operationCount = _paths.Rows.Count;
        try
        {
            await using (FileStream output = new FileStream(
                stagingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await WriteArchiveAsync(
                        output,
                        planned,
                        compressionLevel,
                        OpenArchiveSourceFromDisk,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _paths.Rows.Add(new JournalOperation(PendingChangeKind.Add, archive, stagingPath));
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            StagingFile.TryDelete(stagingPath);
            throw;
        }
    }

    private async Task ExportArchiveCoreAsync(
        IReadOnlyList<ArchiveRoot> roots,
        string externalArchivePath,
        CompressionLevel compressionLevel,
        bool validateNames,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string destinationPath = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalArchivePath);
        foreach (ArchiveRoot root in roots)
        {
            StagingRules.EnsureNotMetadataFolder(_workFolder, root.SourcePath);
            if (root.IsDirectory)
            {
                continue;
            }

            if (File.Exists(root.SourcePath) && IsReparsePoint(root.SourcePath))
            {
                throw new InvalidOperationException("シンボリックリンクは ZIP に入れられません: " + root.SourcePath);
            }

            // 入力が無いときは、書き出し先より先に知らせる
            await using FileStream probe = OpenExportSource(root.SourcePath);
        }

        EnsureExportDestination(destinationPath);
        List<PlannedArchiveEntry> planned = PlanArchiveEntries(roots, cancellationToken);
        if (validateNames)
        {
            ValidateArchiveNames(planned);
        }

        FileStream output = OpenNewExportArchive(destinationPath);
        try
        {
            await using (output.ConfigureAwait(false))
            {
                await WriteArchiveAsync(
                        output,
                        planned,
                        compressionLevel,
                        OpenExportSource,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            StagingFile.TryDelete(destinationPath);
            throw;
        }
    }

    private List<PlannedArchiveEntry> PlanArchiveEntries(
        IReadOnlyList<ArchiveRoot> roots,
        CancellationToken cancellationToken)
    {
        List<PlannedArchiveEntry> planned = new List<PlannedArchiveEntry>();
        foreach (ArchiveRoot root in roots)
        {
            if (!root.IsDirectory)
            {
                planned.Add(new PlannedArchiveEntry(root.EntryName, root.SourcePath));
                continue;
            }

            string prefix = root.EntryName.Length > 0 ? root.EntryName + "/" : string.Empty;
            bool plannedChild = !IsReparsePoint(root.SourcePath)
                && PlanArchivedTree(root.SourcePath, root.SourcePath, prefix, planned, cancellationToken);
            if (!plannedChild && prefix.Length > 0)
            {
                planned.Add(new PlannedArchiveEntry(prefix, null));
            }
        }

        return planned;
    }

    private sealed record ArchiveRoot(string SourcePath, bool IsDirectory, string EntryName);

    private sealed record PlannedArchiveEntry(string Name, string? FilePath);
}
