using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Txfio.Tests.Text;

public sealed class TextOnInterfaceTests
{
    /// <summary>
    /// A hand-written ITransaction can receive WriteAllTextAsync.
    /// </summary>
    /// <remarks>
    /// <para>Given: an ITransaction that is not the concrete Transaction.</para>
    /// <para>When: WriteAllTextAsync is called.</para>
    /// <para>Then: no ArgumentException, and it records the path and string passed.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_HandWrittenTransactionReceivesCall()
    {
        RecordingTransaction transaction = new RecordingTransaction();

        await transaction.WriteAllTextAsync("a.txt", "hello");

        Assert.Equal("a.txt", transaction.Path);
        Assert.Equal("hello", transaction.Text);
    }

    /// <summary>
    /// A hand-written ITransaction can return a string from ReadAllTextAsync.
    /// </summary>
    /// <remarks>
    /// <para>Given: an ITransaction that holds a read result.</para>
    /// <para>When: ReadAllTextAsync is called.</para>
    /// <para>Then: the recorded string is returned without going through a stream.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_HandWrittenTransactionReturnsString()
    {
        RecordingTransaction transaction = new RecordingTransaction { Text = "hello" };

        string text = await transaction.ReadAllTextAsync("a.txt");

        Assert.Equal("hello", text);
    }

    private sealed class RecordingTransaction : ITransaction
    {
        public string? Path { get; private set; }

        public string? Text { get; set; }

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
        {
            _ = (path, cancellationToken);
            return Task.FromResult(Text ?? string.Empty);
        }

        public Task WriteAllTextAsync(
            string path,
            string? contents,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Path = path;
            Text = contents;
            return Task.CompletedTask;
        }

        public Task AddAsync(
            string path,
            Stream content,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, content, progress, cancellationToken);
        }

        public Task UpdateAsync(
            string path,
            Stream content,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, content, progress, cancellationToken);
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending(path, cancellationToken);
        }

        public Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending(path, cancellationToken);
        }

        public Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
        {
            return Pending(oldPath, newPath, cancellationToken);
        }

        public Task MoveAsync(string oldPath, string newPath, bool overwrite, CancellationToken cancellationToken = default)
        {
            return Pending(oldPath, newPath, overwrite, cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending(path, cancellationToken);
        }

        public Task CopyAsync(
            string source,
            string destination,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(source, destination, progress, cancellationToken);
        }

        public Task ImportAsync(
            string externalPath,
            string targetPath,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(externalPath, targetPath, progress, cancellationToken);
        }

        public Task ExportAsync(
            string path,
            string externalPath,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, externalPath, progress, cancellationToken);
        }

        public Task CreateArchiveAsync(
            string source,
            string archivePath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            bool includeBaseDirectory = false,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(source, archivePath, compressionLevel, includeBaseDirectory, progress, cancellationToken);
        }

        public Task CreateArchiveAsync(
            IEnumerable<ArchiveEntrySource> entries,
            string archivePath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(entries, archivePath, compressionLevel, progress, cancellationToken);
        }

        public Task ExportArchiveAsync(
            string source,
            string externalArchivePath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            bool includeBaseDirectory = false,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(source, externalArchivePath, compressionLevel, includeBaseDirectory, progress, cancellationToken);
        }

        public Task ExportArchiveAsync(
            IEnumerable<ArchiveEntrySource> entries,
            string externalArchivePath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(entries, externalArchivePath, compressionLevel, progress, cancellationToken);
        }

        public Task ExtractArchiveAsync(
            string archivePath,
            string destinationDir,
            Encoding? entryNameEncoding = null,
            long? maxExtractedBytes = null,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(archivePath, destinationDir, entryNameEncoding, maxExtractedBytes, progress, cancellationToken);
        }

        public Task ImportArchiveAsync(
            string externalArchivePath,
            string destinationDir,
            Encoding? entryNameEncoding = null,
            long? maxExtractedBytes = null,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(externalArchivePath, destinationDir, entryNameEncoding, maxExtractedBytes, progress, cancellationToken);
        }

        public Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending<Stream>(path, cancellationToken);
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending<bool>(path, cancellationToken);
        }

        public Task<IReadOnlyList<DirectoryEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            return Pending<IReadOnlyList<DirectoryEntry>>(directoryPath, cancellationToken);
        }

        public Task AppendAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, cancellationToken);
        }

        public Task AppendAllTextAsync(string path, string? contents, Encoding encoding, CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, encoding, cancellationToken);
        }

        public Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, cancellationToken);
        }

        public Task AppendAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, encoding, cancellationToken);
        }

        public Task<string> ReadAllTextAsync(
            string path,
            Encoding encoding,
            CancellationToken cancellationToken = default)
        {
            return Pending<string>(path, encoding, cancellationToken);
        }

        public Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default)
        {
            return Pending<string[]>(path, cancellationToken);
        }

        public Task<string[]> ReadAllLinesAsync(
            string path,
            Encoding encoding,
            CancellationToken cancellationToken = default)
        {
            return Pending<string[]>(path, encoding, cancellationToken);
        }

        public Task WriteAllTextAsync(
            string path,
            string? contents,
            Encoding encoding,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, encoding, cancellationToken);
        }

        public Task WriteAllLinesAsync(
            string path,
            IEnumerable<string> contents,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, cancellationToken);
        }

        public Task WriteAllLinesAsync(
            string path,
            IEnumerable<string> contents,
            Encoding encoding,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, contents, encoding, cancellationToken);
        }

        public Task<T?> ReadFromJsonAsync<T>(
            string path,
            JsonSerializerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Pending<T?>(path, options, cancellationToken);
        }

        public Task WriteAsJsonAsync<T>(
            string path,
            T value,
            JsonSerializerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Pending(path, value, options, cancellationToken);
        }

        public Task<CommitReport> CommitAsync(CancellationToken cancellationToken = default)
        {
            return Pending<CommitReport>(cancellationToken);
        }

        public IReadOnlyList<PendingChange> GetPendingChanges()
        {
            throw new NotImplementedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private static Task Pending(params object?[] arguments)
        {
            _ = arguments;
            throw new NotImplementedException();
        }

        private static Task<T> Pending<T>(params object?[] arguments)
        {
            _ = arguments;
            throw new NotImplementedException();
        }
    }
}
