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
        int operationCount = _operations.Count;
        int directoryCount = _createdDirectories.Count;
        try
        {
            CreateCopyDirectory(destination);
            ExtractProgress tracker = new ExtractProgress(progress, totalBytes);
            foreach (ArchiveEntryPlan plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination, plan.RelativePath));
                if (plan.IsDirectory)
                {
                    EnsureExtractDirectory(destination, path);
                    continue;
                }

                EnsureExtractDirectory(destination, System.IO.Path.GetDirectoryName(path)!);
                long bytes = await ExtractFileAsync(plan.Entry, path, tracker, cancellationToken)
                    .ConfigureAwait(false);
                tracker.CompleteFile(bytes);
            }

            if (!tracker.Reported)
            {
                progress?.Report(new TransferProgress(0, totalBytes));
            }

            if (_operations.Count > operationCount)
            {
                await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            DeleteCreatedDirectoriesFrom(directoryCount, ignoreIoFailures: true);
            throw;
        }
    }

    private void EnsureExtractDirectory(string destination, string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        string? parent = System.IO.Path.GetDirectoryName(path);
        if (parent is not null
            && !string.Equals(parent, destination, StringComparison.OrdinalIgnoreCase))
        {
            EnsureExtractDirectory(destination, parent);
        }

        CreateCopyDirectory(path);
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

        _operations.Add(new JournalOperation(PendingChangeKind.Add, path, stagingPath));
        File.SetLastWriteTimeUtc(stagingPath, entry.LastWriteTime.UtcDateTime);
        return new FileInfo(stagingPath).Length;
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
