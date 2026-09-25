using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class OverlappingCallTests
{
    /// <summary>
    /// 重なった Add は拒否し、先の Add は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 先の Add が内容の読み取りで待っている</para>
    /// <para>手順: 別の Add を重ね、待っていた Add を終わらせ、そのあとにもう 1 件 Add する</para>
    /// <para>期待: 重なった Add は InvalidOperationException で記録されず、先の Add とあとの Add だけが残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_重なるとInvalidOperationExceptionで先の操作は残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using HoldStream held = new HoldStream();
        Task first = tx.AddAsync("a.txt", held);
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        InvalidOperationException overlap = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.AddAsync("b.txt", LeftoverAddFiles.Utf8Stream("other")));
        held.Release();
        await first;
        await using MemoryStream later = LeftoverAddFiles.Utf8Stream("later");
        await tx.AddAsync("c.txt", later);

        Assert.Contains("呼び出しが重なっています", overlap.Message, StringComparison.Ordinal);
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(tx.GetPendingChanges(), change => change.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(tx.GetPendingChanges(), change => change.Path.EndsWith("c.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(tx.GetPendingChanges(), change => change.Path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 進行中の一覧取得と破棄は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: Add が内容の読み取りで待っている</para>
    /// <para>手順: GetPendingChanges と DisposeAsync を重ね、待っていた Add を終わらせてから破棄する</para>
    /// <para>期待: 重なった呼び出しは InvalidOperationException で、Add は残り、終わったあとの破棄はできる</para>
    /// </remarks>
    [Fact]
    public async Task GetPendingChanges_進行中だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        try
        {
            await using HoldStream held = new HoldStream();
            Task first = tx.AddAsync("a.txt", held);
            await held.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            InvalidOperationException pending = Assert.Throws<InvalidOperationException>(() => tx.GetPendingChanges());
            InvalidOperationException dispose = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DisposeAsync().AsTask());
            held.Release();
            await first;

            Assert.Contains("呼び出しが重なっています", pending.Message, StringComparison.Ordinal);
            Assert.Contains("呼び出しが重なっています", dispose.Message, StringComparison.Ordinal);
            Assert.Single(tx.GetPendingChanges());
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// 進行中の progress から同じトランザクションを呼ぶと拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: なし</para>
    /// <para>手順: Add の progress から別の Add を呼ぶ</para>
    /// <para>期待: InvalidOperationException で、どちらのファイルも記録されない</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_progressから重なるとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        SynchronousProgress progress = new SynchronousProgress(_ =>
        {
            tx.AddAsync("b.txt", LeftoverAddFiles.Utf8Stream("other")).GetAwaiter().GetResult();
        });
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");

        InvalidOperationException overlap = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.AddAsync("a.txt", content, progress));

        Assert.Contains("呼び出しが重なっています", overlap.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 別のトランザクションの Add は同時に進む
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションが 2 つある</para>
    /// <para>手順: 両方の Add が内容の読み取りで待つところまで進めてから、両方を終わらせる</para>
    /// <para>期待: どちらも例外にならず、それぞれの pending が 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_別トランザクションなら同時に進むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction left = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction right = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using HoldStream leftHeld = new HoldStream();
        await using HoldStream rightHeld = new HoldStream();
        Task leftAdd = left.AddAsync("a.txt", leftHeld);
        Task rightAdd = right.AddAsync("b.txt", rightHeld);

        await leftHeld.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await rightHeld.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        leftHeld.Release();
        rightHeld.Release();
        await Task.WhenAll(leftAdd, rightAdd);

        Assert.Single(left.GetPendingChanges());
        Assert.Single(right.GetPendingChanges());
    }

    private sealed class SynchronousProgress : IProgress<TransferProgress>
    {
        private readonly Action<TransferProgress> _report;

        public SynchronousProgress(Action<TransferProgress> report)
        {
            _report = report;
        }

        public void Report(TransferProgress value)
        {
            _report(value);
        }
    }

    private sealed class HoldStream : Stream
    {
        private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pending = 1;

        public Task Entered => _entered.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _pending, 0) == 0)
            {
                return 0;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (buffer.Length == 0)
            {
                return 0;
            }

            buffer.Span[0] = (byte)'x';
            return 1;
        }
    }
}
