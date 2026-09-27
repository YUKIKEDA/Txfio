using System.IO.Compression;
using System.Text;

namespace Txfio;

/// <content>
/// Extracting ZIP archives (ZIPs inside and outside the work folder).
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task ExtractArchiveAsync(
        string archivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maxExtractedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        }

        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string archive = WorkPath.ResolveInWorkFolder(_workFolder, archivePath);
        StagingRules.EnsureNotMetadataFolder(_workFolder, archive);
        string destination = ValidateExtractDestination(destinationDir);
        await AcquireExtractLocksAsync(destination).ConfigureAwait(false);
        await using Stream content = ReadCore(archive, cancellationToken);
        await ExtractAsync(content, destination, entryNameEncoding, maxExtractedBytes, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ImportArchiveAsync(
        string externalArchivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maxExtractedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        }

        using CallScope scope = EnterCall(cancellationToken);
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();
        string external = WorkPath.ResolveOutsideWorkFolder(_workFolder, externalArchivePath);
        string destination = ValidateExtractDestination(destinationDir);
        if (Directory.Exists(external))
        {
            throw new UnsupportedOperationException("A directory cannot be opened as a ZIP: " + external);
        }

        await AcquireExtractLocksAsync(destination).ConfigureAwait(false);
        await using FileStream content = OpenExternalFile(
            external,
            "The ZIP does not exist: " + external,
            external);
        await ExtractAsync(content, destination, entryNameEncoding, maxExtractedBytes, progress, cancellationToken)
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

    private async Task AcquireExtractLocksAsync(string destination)
    {
        await _locks.AcquireSharedAsync(_workFolder, _lockAttempt).ConfigureAwait(false);
        await _locks.AcquireReservingAsync(_workFolder, new[] { destination }, new[] { destination }, _lockAttempt).ConfigureAwait(false);
    }

    private async Task ExtractAsync(
        Stream content,
        string destination,
        Encoding? entryNameEncoding,
        long? maxExtractedBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using ZipArchive zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding);
        IReadOnlyList<ArchiveEntryPlan> plans = ArchiveEntryNames.Plan(zip.Entries);
        long totalBytes = 0;
        foreach (ArchiveEntryPlan plan in plans)
        {
            if (plan.IsDirectory)
            {
                continue;
            }

            long length = plan.Entry.Length;
            if (length < 0 || totalBytes > long.MaxValue - length)
            {
                throw new InvalidDataException("The total extracted size of the ZIP exceeds the limit");
            }

            totalBytes += length;
        }

        // Stop before writing by the declared total; Stored entries can read more than Length, so also stop at the limit while writing.
        ExtractByteBudget? budget = null;
        if (maxExtractedBytes is long limit)
        {
            if (totalBytes > limit)
            {
                throw new InvalidDataException(
                    "The total extracted size of the ZIP exceeds the limit: " + totalBytes + " > " + limit);
            }

            budget = new ExtractByteBudget(limit);
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
                (index, tracker, token) => ExtractFileAsync(
                    files[index].Entry,
                    files[index].Path,
                    budget,
                    tracker,
                    token),
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
        ExtractByteBudget? budget,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        string stagingPath = WorkPath.StagingFilePath(path, _transactionId);
        long written;
        Stream entryStream = entry.Open();
        if (budget is not null)
        {
            entryStream = new CappedEntryStream(entryStream, budget);
        }

        await using (entryStream.ConfigureAwait(false))
        {
            written = await StagingFile.WriteAsync(stagingPath, entryStream, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        File.SetLastWriteTimeUtc(stagingPath, entry.LastWriteTime.UtcDateTime);
        return written;
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

    /// <summary>
    /// The bytes that the extract may still read.
    /// </summary>
    private sealed class ExtractByteBudget
    {
        private readonly long _limit;
        private readonly byte[] _probe = new byte[1];
        private long _accepted;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExtractByteBudget"/> class with the limit.
        /// </summary>
        /// <param name="limit">The total bytes that may be read.</param>
        internal ExtractByteBudget(long limit)
        {
            _limit = limit;
        }

        /// <summary>
        /// Reads only within the limit (throws <see cref="InvalidDataException"/> if there are bytes beyond it).
        /// </summary>
        /// <param name="inner">The content of the entry.</param>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="offset">The start position in the buffer.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>The number of bytes read (0 at the end).</returns>
        internal int Read(Stream inner, byte[] buffer, int offset, int count)
        {
            bool probe = BeginRead(count, out int toRead);
            int read = probe
                ? inner.Read(_probe, 0, 1)
                : inner.Read(buffer, offset, toRead);
            return Finish(read, probe);
        }

        /// <summary>
        /// Reads asynchronously, only within the limit (throws <see cref="InvalidDataException"/> if there are bytes beyond it).
        /// </summary>
        /// <param name="inner">The content of the entry.</param>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="cancellationToken">The token to cancel the operation.</param>
        /// <returns>The number of bytes read (0 at the end).</returns>
        internal async ValueTask<int> ReadAsync(
            Stream inner,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            bool probe = BeginRead(buffer.Length, out int toRead);
            int read = probe
                ? await inner.ReadAsync(_probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false)
                : await inner.ReadAsync(buffer.Slice(0, toRead), cancellationToken).ConfigureAwait(false);
            return Finish(read, probe);
        }

        private bool BeginRead(int requested, out int toRead)
        {
            long room = _limit - _accepted;
            if (room <= 0)
            {
                toRead = 1;
                return true;
            }

            toRead = room > requested ? requested : (int)room;
            return false;
        }

        private int Finish(int read, bool probe)
        {
            if (probe)
            {
                if (read > 0)
                {
                    throw Exceeded();
                }

                return 0;
            }

            _accepted += read;
            return read;
        }

        private InvalidDataException Exceeded()
        {
            return new InvalidDataException(
                "The total extracted size of the ZIP exceeds the limit: " + (_accepted + 1) + " > " + _limit);
        }
    }

    /// <summary>
    /// Stops reading an entry at the limit for the whole extract.
    /// </summary>
    private sealed class CappedEntryStream : Stream
    {
        private readonly Stream _inner;
        private readonly ExtractByteBudget _budget;

        /// <summary>
        /// Initializes a new instance of the <see cref="CappedEntryStream"/> class with the content and the limit shared by the whole extract.
        /// </summary>
        /// <param name="inner">The content of the entry (this stream disposes it).</param>
        /// <param name="budget">The limit for the whole extract.</param>
        internal CappedEntryStream(Stream inner, ExtractByteBudget budget)
        {
            _inner = inner;
            _budget = budget;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return await _budget.ReadAsync(_inner, buffer, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            return _budget.Read(_inner, buffer, offset, count);
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
