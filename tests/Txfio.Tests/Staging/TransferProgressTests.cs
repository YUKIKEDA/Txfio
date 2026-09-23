using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class TransferProgressTests
{
    /// <summary>
    /// バッファを超える Add は書き終えたバイト数を順に通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 81921 バイトのシークできる内容がある</para>
    /// <para>手順: 進捗を渡して AddAsync する</para>
    /// <para>期待: 81920 のあと 81921 が通知され、どちらも TotalBytes は 81921 で、ステージングファイルもその長さである</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_バッファごとにBytesCopiedが進むこと()
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
    /// 途中から読んだ Update は残りの長さを通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルがあり、内容ストリームの位置は 2 である</para>
    /// <para>手順: 進捗を渡して UpdateAsync する</para>
    /// <para>期待: 通知は残り 5 バイトの 1 回で、ステージングされるのはその 5 バイトである</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_開始位置からの残りを通知すること()
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
    /// 空の内容は 0 バイトを 1 回通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さ 0 のシークできる内容がある</para>
    /// <para>手順: 進捗を渡して AddAsync する</para>
    /// <para>期待: 通知は (0, 0) の 1 回である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_空の内容は0を1回通知すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using MemoryStream content = new MemoryStream();
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AddAsync("a.txt", content, progress);

        Assert.Equal(new TransferProgress(0, 0), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// シークできない内容は残りバイト数を持たない
    /// </summary>
    /// <remarks>
    /// <para>前提: シークできない 4 バイトの内容と、空のシークできない内容がある</para>
    /// <para>手順: それぞれ進捗を渡して AddAsync する</para>
    /// <para>期待: TotalBytes はどちらも null で、BytesCopied は 4 と 0 である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_シークできないとTotalBytesはnullであること()
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
    /// 長さが位置より小さいときは残りを不明とする
    /// </summary>
    /// <remarks>
    /// <para>前提: Length が -1 を返す内容がある</para>
    /// <para>手順: 進捗を渡して AddAsync する</para>
    /// <para>期待: TotalBytes は null で、BytesCopied は読めたバイト数である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_残りが負ならTotalBytesはnullであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ShrinkingLengthStream content = new ShrinkingLengthStream(new byte[] { 1, 2, 3 });
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AddAsync("a.txt", content, progress);

        Assert.Equal(new TransferProgress(3, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// 再ステージの通知は新しい内容だけである
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add している</para>
    /// <para>手順: 別の内容で UpdateAsync し、進捗を受け取る</para>
    /// <para>期待: 通知は新しい 5 バイトだけで、退避した古い内容の長さは含まない</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_再ステージは新しい内容だけ通知すること()
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
    /// 進捗の Report が例外を投げるとステージングは残らない
    /// </summary>
    /// <remarks>
    /// <para>前提: 進捗の Report が例外を投げる</para>
    /// <para>手順: AddAsync する</para>
    /// <para>期待: InvalidOperationException になり、未確定操作と .txnew は残らない</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_Reportが失敗するとtxnewは残らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using MemoryStream content = new MemoryStream(new byte[] { 1, 2, 3 });
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("a.txt", content, new ThrowingProgress()));

        Assert.Empty(tx.GetPendingChanges());
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// コピーの途中で取り消すと、それまでの通知のあと失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 最初の通知で取り消しトークンが取り消される</para>
    /// <para>手順: そのトークンで AddAsync する</para>
    /// <para>期待: 通知が 1 回以上あり、OperationCanceledException になり、.txnew は残らない</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_コピー中に取り消すとOperationCanceledExceptionになること()
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
