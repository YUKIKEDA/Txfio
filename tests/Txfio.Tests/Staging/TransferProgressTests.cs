using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class TransferProgressTests
{
    /// <summary>
    /// An Add larger than the buffer reports the bytes written in order.
    /// </summary>
    /// <remarks>
    /// <para>Given: seekable content of 81921 bytes.</para>
    /// <para>When: AddAsync is called with a progress receiver.</para>
    /// <para>Then: 81920 and then 81921 are reported, both with TotalBytes 81921, and the staging file has that length.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_BytesCopiedAdvancesPerBuffer()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] data = new byte[81921];
        data[81920] = 1;
        await using MemoryStream content = new MemoryStream(data);
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AddAsync("a.txt", content, progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(new TransferProgress(81920, 81921), progress.Reports[0]);
        Assert.Equal(new TransferProgress(81921, 81921), progress.Reports[1]);
        string staged = Assert.Single(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Equal(81921, new FileInfo(staged).Length);
    }

    /// <summary>
    /// An Update read from the middle reports the remaining length.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file, and the content stream is at position 2.</para>
    /// <para>When: UpdateAsync is called with a progress receiver.</para>
    /// <para>Then: one report of the remaining 5 bytes, and those 5 bytes are staged.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_ReportsRemainingFromStartPosition()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using MemoryStream content = new MemoryStream(Encoding.UTF8.GetBytes("xxhello"));
        content.Position = 2;
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.UpdateAsync("a.txt", content, progress);

        Assert.Equal(new TransferProgress(5, 5), Assert.Single(progress.Reports));
        string staged = Assert.Single(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Equal("hello", await File.ReadAllTextAsync(staged));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Empty content reports 0 bytes once.
    /// </summary>
    /// <remarks>
    /// <para>Given: seekable content of length 0.</para>
    /// <para>When: AddAsync is called with a progress receiver.</para>
    /// <para>Then: one report of (0, 0).</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_EmptyContentReportsZeroOnce()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using MemoryStream content = new MemoryStream();
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AddAsync("a.txt", content, progress);

        Assert.Equal(new TransferProgress(0, 0), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// Content that cannot seek has no remaining byte count.
    /// </summary>
    /// <remarks>
    /// <para>Given: 4 bytes of non-seekable content, and empty non-seekable content.</para>
    /// <para>When: AddAsync is called on each with a progress receiver.</para>
    /// <para>Then: TotalBytes is null for both, and BytesCopied is 4 and 0.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_NonSeekableHasNullTotalBytes()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ProgressList filled = new ProgressList();
        await using ForwardOnlyStream content = new ForwardOnlyStream(new byte[] { 1, 2, 3, 4 });
        await tx.AddAsync("a.txt", content, filled);
        ProgressList empty = new ProgressList();
        await using ForwardOnlyStream blank = new ForwardOnlyStream(Array.Empty<byte>());

        await tx.AddAsync("b.txt", blank, empty);

        Assert.Equal(new TransferProgress(4, null), Assert.Single(filled.Reports));
        Assert.Equal(new TransferProgress(0, null), Assert.Single(empty.Reports));
    }

    /// <summary>
    /// When the length is smaller than the position, the remainder is unknown.
    /// </summary>
    /// <remarks>
    /// <para>Given: content whose Length returns -1.</para>
    /// <para>When: AddAsync is called with a progress receiver.</para>
    /// <para>Then: TotalBytes is null, and BytesCopied is the bytes that could be read.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_NegativeRemainderHasNullTotalBytes()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ShrinkingLengthStream content = new ShrinkingLengthStream(new byte[] { 1, 2, 3 });
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AddAsync("a.txt", content, progress);

        Assert.Equal(new TransferProgress(3, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// A restage reports only the new content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added.</para>
    /// <para>When: UpdateAsync is called with different content, receiving progress.</para>
    /// <para>Then: only the new 5 bytes are reported, not the length of the old content that was backed up.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_RestageReportsOnlyNewContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = new MemoryStream(Encoding.UTF8.GetBytes("old"));
        await tx.AddAsync("a.txt", first);
        await using MemoryStream newer = new MemoryStream(Encoding.UTF8.GetBytes("newer"));
        ProgressList progress = new ProgressList();

        await tx.UpdateAsync("a.txt", newer, progress);

        Assert.Equal(new TransferProgress(5, 5), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// When the progress Report throws, nothing stays staged.
    /// </summary>
    /// <remarks>
    /// <para>Given: the progress Report throws.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: InvalidOperationException, and neither a pending operation nor a .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ReportFailureLeavesNoTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using MemoryStream content = new MemoryStream(new byte[] { 1, 2, 3 });
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("a.txt", content, new ThrowingProgress()));

        Assert.Empty(tx.GetPendingChanges());
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Canceling during the copy fails after the reports so far.
    /// </summary>
    /// <remarks>
    /// <para>Given: the cancellation token is canceled at the first report.</para>
    /// <para>When: AddAsync is called with that token.</para>
    /// <para>Then: at least one report, OperationCanceledException, and no .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_CancelDuringCopyThrowsOperationCanceledException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using MemoryStream content = new MemoryStream(new byte[] { 1, 2, 3 });
        using CancellationTokenSource source = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tx.AddAsync("a.txt", content, progress, source.Token));

        Assert.NotEmpty(progress.Reports);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    private sealed class ProgressList : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => throw new InvalidOperationException("report");
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _source;

        public CancelOnReport(CancellationTokenSource source) => _source = source;

        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value)
        {
            Reports.Add(value);
            _source.Cancel();
        }
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;

        private int _offset;

        public ForwardOnlyStream(byte[] data) => _data = data;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int countToCopy = Math.Min(count, _data.Length - _offset);
            if (countToCopy > 0)
            {
                Buffer.BlockCopy(_data, _offset, buffer, offset, countToCopy);
                _offset += countToCopy;
            }

            return countToCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int countToCopy = Math.Min(buffer.Length, _data.Length - _offset);
            if (countToCopy > 0)
            {
                _data.AsSpan(_offset, countToCopy).CopyTo(buffer.Span);
                _offset += countToCopy;
            }

            return ValueTask.FromResult(countToCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ShrinkingLengthStream : MemoryStream
    {
        public ShrinkingLengthStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override long Length => -1;
    }
}
