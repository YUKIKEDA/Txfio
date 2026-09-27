using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class OverlappingCallTests
{
    /// <summary>
    /// An overlapping Add is rejected, and the earlier Add remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: an earlier Add is waiting while reading its content.</para>
    /// <para>When: another Add overlaps, the waiting Add finishes, and then one more Add is made.</para>
    /// <para>Then: the overlapping Add throws InvalidOperationException and is not recorded, and only the earlier and later Adds remain.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_OverlapThrowsInvalidOperationExceptionAndKeepsEarlierOperation()
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

        Assert.Contains("Calls to the same transaction overlap", overlap.Message, StringComparison.Ordinal);
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(tx.GetPendingChanges(), change => change.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(tx.GetPendingChanges(), change => change.Path.EndsWith("c.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(tx.GetPendingChanges(), change => change.Path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Listing and disposing while a call is in progress are rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is waiting while reading its content.</para>
    /// <para>When: GetPendingChanges and DisposeAsync overlap, the waiting Add finishes, and then the transaction is disposed.</para>
    /// <para>Then: the overlapping calls throw InvalidOperationException, the Add remains, and disposing after it finishes works.</para>
    /// </remarks>
    [Fact]
    public async Task GetPendingChanges_InProgressThrowsInvalidOperationException()
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

            Assert.Contains("Calls to the same transaction overlap", pending.Message, StringComparison.Ordinal);
            Assert.Contains("Calls to the same transaction overlap", dispose.Message, StringComparison.Ordinal);
            Assert.Single(tx.GetPendingChanges());
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Calling the same transaction from a progress callback in progress is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: nothing.</para>
    /// <para>When: another Add is called from the progress of an Add.</para>
    /// <para>Then: InvalidOperationException, and neither file is recorded.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_OverlapFromProgressThrowsInvalidOperationException()
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

        Assert.Contains("Calls to the same transaction overlap", overlap.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Adds of different transactions proceed at the same time.
    /// </summary>
    /// <remarks>
    /// <para>Given: two transactions.</para>
    /// <para>When: both Adds proceed until they wait while reading their content, then both finish.</para>
    /// <para>Then: neither throws, and each has one pending change.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DifferentTransactionsProceedConcurrently()
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
