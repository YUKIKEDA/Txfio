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
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string archive = WorkPath.ResolveInWorkFolder(_workFolder, archivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, archive);
        string destination = ValidateExtractDestination(destinationDir);
        await using PathLockSet.WorkFolderExclusive exclusive = await AcquireExtractLocksAsync(destination).ConfigureAwait(false);
        await using Stream content = ReadCore(archive, cancellationToken);
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
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        BeginLockAttempt(cancellationToken);
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string external = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalArchivePath);
        string destination = ValidateExtractDestination(destinationDir);
        if (Directory.Exists(external))
        {
            throw new UnsupportedOperationException("ディレクトリは ZIP として開けません: " + external);
        }

        await using PathLockSet.WorkFolderExclusive exclusive = await AcquireExtractLocksAsync(destination).ConfigureAwait(false);
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
        StagingRules.ThrowIfInsideDeleteTree(_paths.Rows, destination);
        StagingRules.ThrowIfInsideDirectoryMove(_paths.Rows, destination);
        StagingRules.ThrowIfTouchesDeletedDirectory(_paths.Rows, destination);
        StagingRules.ThrowIfOperationUnderDirectory(_paths.Rows, destination);
        ThrowIfCopyPathIsStaged(destination);
        EnsureCopyDestinationFree(destination);
        return destination;
    }

    private async Task<PathLockSet.WorkFolderExclusive> AcquireExtractLocksAsync(string destination)
    {
        await _locks.AcquireSharedAsync(_workFolder).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { destination }, destination).ConfigureAwait(false);
        return await _locks.EnterExclusiveAsync(_workFolder).ConfigureAwait(false);
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

        string[] destinations = new string[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            destinations[i] = files[i].Path;
        }

        await ApplyStagedTreeAsync(
                directories,
                destinations,
                (index, tracker, token) => ExtractFileAsync(files[index].Entry, files[index].Path, tracker, token),
                progress,
                totalBytes,
                cancellationToken)
            .ConfigureAwait(false);
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
}
