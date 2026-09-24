using System.IO.Compression;
using System.Text;

namespace Txfio;

/// <content>
/// ZIP アーカイブの展開（ワークフォルダ内の ZIP と外の ZIP）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task ExtractArchiveAsync(
        string archivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string archive = WorkPath.ResolveInWorkFolder(_workFolder, archivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, archive);
        string destination = ValidateExtractDestination(destinationDir);
        AcquireExtractLocks(destination);
        await using Stream content = await ReadAsync(archive, cancellationToken).ConfigureAwait(false);
        await ExtractAsync(content, destination, entryNameEncoding, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ImportArchiveAsync(
        string externalArchivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string external = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalArchivePath);
        string destination = ValidateExtractDestination(destinationDir);
        if (Directory.Exists(external))
        {
            throw new UnsupportedOperationException("ディレクトリは ZIP として開けません: " + external);
        }

        AcquireExtractLocks(destination);
        await using FileStream content = OpenExternalFile(
            external,
            "ZIP が存在しません: " + external,
            external);
        await ExtractAsync(content, destination, entryNameEncoding, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private string ValidateExtractDestination(string destinationDir)
    {
        string destination = WorkPath.ResolveInWorkFolder(_workFolder, destinationDir);
        StagingRules.EnsureNotMetadataFolder(_workFolder, destination);
        StagingRules.ThrowIfInsideDeleteTree(_operations, destination);
        StagingRules.ThrowIfInsideDirectoryMove(_operations, destination);
        StagingRules.ThrowIfTouchesDeletedDirectory(_operations, destination);
        StagingRules.ThrowIfOperationUnderDirectory(_operations, destination);
        ThrowIfCopyPathIsStaged(destination);
        EnsureCopyDestinationFree(destination);
        return destination;
    }

    private void AcquireExtractLocks(string destination)
    {
        _locks.AcquireExclusive(_workFolder);
        _locks.RejectForeignLocks(_workFolder);
        _locks.Acquire(_workFolder, destination);
    }

    private async Task ExtractAsync(
        Stream content,
        string destination,
        Encoding? entryNameEncoding,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using ZipArchive zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding);
        IReadOnlyList<ArchiveEntryPlan> plans = ArchiveEntryNames.Plan(zip.Entries);
        long totalBytes = 0;
        foreach (ArchiveEntryPlan plan in plans)
        {
            if (!plan.IsDirectory)
            {
                totalBytes += plan.Entry.Length;
            }
        }

        EnsureCopyDestinationFree(destination);
        List<string> directories = new List<string> { destination };
        List<PlannedExtractFile> files = new List<PlannedExtractFile>();
        foreach (ArchiveEntryPlan plan in plans)
        {
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination, plan.RelativePath));
            if (plan.IsDirectory)
            {
                RecordExtractDirectory(destination, path, directories);
                continue;
            }

            string? parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                RecordExtractDirectory(destination, parent, directories);
            }

            files.Add(new PlannedExtractFile(plan.Entry, path));
        }

        int operationCount = _operations.Count;
        int directoryCount = _createdDirectories.Count;
        foreach (string directory in directories)
        {
            _createdDirectories.Add(directory);
        }

        foreach (PlannedExtractFile file in files)
        {
            string stagingPath = WorkPath.StagingFilePath(file.Path, _transactionId);
            _operations.Add(new JournalOperation(PendingChangeKind.Add, file.Path, stagingPath));
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            foreach (string directory in directories)
            {
                Directory.CreateDirectory(directory);
            }

            ExtractProgress tracker = new ExtractProgress(progress, totalBytes);
            foreach (PlannedExtractFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long bytes = await ExtractFileAsync(file.Entry, file.Path, tracker, cancellationToken)
                    .ConfigureAwait(false);
                tracker.CompleteFile(bytes);
            }

            if (!tracker.Reported)
            {
                progress?.Report(new TransferProgress(0, totalBytes));
            }
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            DeleteCreatedDirectoriesFrom(directoryCount, ignoreIoFailures: true);
            await TryPersistUndoAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void RecordExtractDirectory(string destination, string path, List<string> directories)
    {
        if (ContainsPath(directories, path))
        {
            return;
        }

        string? parent = System.IO.Path.GetDirectoryName(path);
        if (parent is not null
            && !string.Equals(parent, destination, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
        {
            RecordExtractDirectory(destination, parent, directories);
        }

        directories.Add(path);
    }

    private bool ContainsPath(List<string> directories, string path)
    {
        foreach (string directory in directories)
        {
            if (string.Equals(directory, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<long> ExtractFileAsync(
        ZipArchiveEntry entry,
        string path,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        string stagingPath = WorkPath.StagingFilePath(path, _transactionId);
        Stream entryStream = entry.Open();
        await using (entryStream.ConfigureAwait(false))
        {
            await StagingFile.WriteAsync(stagingPath, entryStream, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        File.SetLastWriteTimeUtc(stagingPath, entry.LastWriteTime.UtcDateTime);
        return new FileInfo(stagingPath).Length;
    }

    private sealed class PlannedExtractFile
    {
        internal PlannedExtractFile(ZipArchiveEntry entry, string path)
        {
            Entry = entry;
            Path = path;
        }

        internal ZipArchiveEntry Entry { get; }

        internal string Path { get; }
    }

    private sealed class ExtractProgress : IProgress<TransferProgress>
    {
        private readonly IProgress<TransferProgress>? _inner;
        private readonly long _totalBytes;
        private long _completed;

        internal ExtractProgress(IProgress<TransferProgress>? inner, long totalBytes)
        {
            _inner = inner;
            _totalBytes = totalBytes;
        }

        internal bool Reported { get; private set; }

        /// <inheritdoc />
        public void Report(TransferProgress value)
        {
            Reported = true;
            _inner?.Report(new TransferProgress(_completed + value.BytesCopied, _totalBytes));
        }

        internal void CompleteFile(long bytes)
        {
            _completed += bytes;
        }
    }
}
