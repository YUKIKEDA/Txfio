using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class LockWaitTests
{
    /// <summary>
    /// Without a wait, a path in use fails right away.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt.</para>
    /// <para>When: the other begins with a zero wait and adds the same path.</para>
    /// <para>Then: LockContentionException without waiting, and Path is a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_FailsImmediatelyOnPathInUseWithZeroWait()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.Zero);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

        long started = Environment.TickCount64;
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.AddAsync("a.txt", content));

        Assert.True(Environment.TickCount64 - started < 500);
        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), contention.Path);
        Assert.Empty(waiter.GetPendingChanges());
    }

    /// <summary>
    /// If the other side disposes during the wait, the operation succeeds.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt, and the other has a 2-second wait.</para>
    /// <para>When: the first transaction is disposed after 200 ms, and the same path is added.</para>
    /// <para>Then: the Add succeeds, and there is one pending operation.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_SucceedsWhenOtherDisposesDuringWait()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (holder)
        {
            await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("a.txt", held);
            await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(2));
            Task release = Task.Run(async () =>
            {
                await Task.Delay(200);
                await holder.DisposeAsync();
            });
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

            await waiter.AddAsync("a.txt", content);
            await release;

            Assert.Single(waiter.GetPendingChanges());
        }
    }

    /// <summary>
    /// If the lock is not free by the deadline it fails, and the next call waits from the same limit again.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt, and the waiting side has a limit of 300 ms.</para>
    /// <para>When: the same path is added and fails, then the other side is disposed and the Add is tried again.</para>
    /// <para>Then: the first is LockContentionException with Path a.txt, and the second succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_CallAfterTimeoutCanWaitAgain()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(300));
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("one");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.AddAsync("a.txt", first));

        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), contention.Path);
        await holder.DisposeAsync();
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("two");
        await waiter.AddAsync("a.txt", second);
        Assert.Single(waiter.GetPendingChanges());
    }

    /// <summary>
    /// Canceling the wait is not contention.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt, and the other waits without a deadline.</para>
    /// <para>When: an Add of the same path is canceled after 150 ms.</para>
    /// <para>Then: OperationCanceledException, the waiting side has no pending changes, and the first lock remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_CancelingWaitThrowsOperationCanceledException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, Timeout.InfiniteTimeSpan);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        Task cancelLater = Task.Run(async () =>
        {
            await Task.Delay(150);
            cancel.Cancel();
        });
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.AddAsync("a.txt", content, cancellationToken: cancel.Token));
        await cancelLater;

        Assert.Empty(waiter.GetPendingChanges());
        await using ITransaction third = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("again");
        LockContentionException stillHeld = await Assert.ThrowsAsync<LockContentionException>(
            () => third.AddAsync("a.txt", again));
        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), stillHeld.Path);
    }

    /// <summary>
    /// While waiting for the second path, the path taken first is kept.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, another transaction has added m.txt, and the waiting side has a limit of 300 ms.</para>
    /// <para>When: a.txt is moved to m.txt.</para>
    /// <para>Then: LockContentionException with Path m.txt, no pending changes, and a.txt cannot be added from another transaction.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_KeepsFirstLockWhenSecondTimesOut()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("m.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(300));

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.MoveAsync("a.txt", "m.txt"));

        Assert.Equal(System.IO.Path.Combine(work.Path, "m.txt"), contention.Path);
        Assert.Empty(waiter.GetPendingChanges());
        await using ITransaction third = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("again");
        await Assert.ThrowsAsync<LockContentionException>(() => third.AddAsync("a.txt", again));
    }

    /// <summary>
    /// While waiting for an exclusive lock, the work-folder lock is not held, so waiters do not block each other.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt, sub exists, and the two waiters for exclusive locks each have a limit of 5 seconds.</para>
    /// <para>When: two DeleteTrees start on separate threads, the first transaction is disposed, the one that proceeded is disposed, and then the other is awaited.</para>
    /// <para>Then: both succeed within 3 seconds.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_DoesNotHoldWorkFolderLockWhileWaitingForExclusive()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
        Task firstWait = Task.Run(async () => await first.DeleteTreeAsync("sub"));
        await Task.Delay(200);
        Task secondWait = Task.Run(async () => await second.DeleteTreeAsync("sub"));
        await Task.Delay(200);

        await holder.DisposeAsync();
        Task finished = await Task.WhenAny(firstWait, secondWait).WaitAsync(TimeSpan.FromSeconds(3));
        await finished;
        if (finished == firstWait)
        {
            await first.DisposeAsync();
            await secondWait.WaitAsync(TimeSpan.FromSeconds(3));
        }
        else
        {
            await second.DisposeAsync();
            await firstWait.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// When the share-lost marker becomes free, creating a directory succeeds.
    /// </summary>
    /// <remarks>
    /// <para>Given: <c>.txfio/share-lost.lock</c> is open as shared, and the wait is 2 seconds.</para>
    /// <para>When: the handle is closed after 200 ms, and sub is created with CreateDirectory.</para>
    /// <para>Then: sub exists.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_SucceedsWhenMarkerBecomesFree()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        FileStream held = new FileStream(
            MetadataNames.ShareLostLockPath(work.Path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(2));
        Task release = Task.Run(async () =>
        {
            await Task.Delay(200);
            await held.DisposeAsync();
        });

        await waiter.CreateDirectoryAsync("sub");
        await release;

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
    }

    /// <summary>
    /// Even when the share-lost marker is in use, creating a directory does not wait for the work-folder lock.
    /// </summary>
    /// <remarks>
    /// <para>Given: <c>.txfio/share-lost.lock</c> is open as shared, and the wait is zero.</para>
    /// <para>When: sub is created with CreateDirectory.</para>
    /// <para>Then: sub exists (only Recover makes the whole work folder exclusive).</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_DoesNotWaitForWorkFolderLockWhenMarkerInUse()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        await using FileStream held = new FileStream(
            MetadataNames.ShareLostLockPath(work.Path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path);

        await waiter.CreateDirectoryAsync("sub");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
        _ = held;
    }

    /// <summary>
    /// Creating a directory does not fail just because a path lock file is open.
    /// </summary>
    /// <remarks>
    /// <para>Given: the .lock of a.txt is open without sharing.</para>
    /// <para>When: sub is created with CreateDirectory.</para>
    /// <para>Then: sub exists.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_DoesNotFailOnPathLockFileAlone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string foreign = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(foreign)!);
        await using FileStream held = new FileStream(foreign, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path);

        await waiter.CreateDirectoryAsync("sub");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
        _ = held;
    }

    /// <summary>
    /// Recovery also waits until the work-folder lock is free.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has added a.txt.</para>
    /// <para>When: Recover with a 300 ms wait fails, the transaction is disposed, and Recover runs with a 2-second wait.</para>
    /// <para>Then: the first is LockContentionException with Path set to the work folder, and the second is NoPendingTransactions.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_WaitsUntilWorkFolderLockIsFree()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromMilliseconds(300)));

        Assert.Equal(System.IO.Path.GetFullPath(work.Path), contention.Path);
        await holder.DisposeAsync();
        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromSeconds(2));
        Assert.Equal(RecoverResult.NoPendingTransactions, report.Result);
    }

    /// <summary>
    /// A negative wait is rejected, and no deadline can begin.
    /// </summary>
    /// <remarks>
    /// <para>Given: a work folder exists.</para>
    /// <para>When: Begin and Recover are called with a negative TimeSpan, and Begin with Timeout.InfiniteTimeSpan.</para>
    /// <para>Then: the negative time throws ArgumentOutOfRangeException, and no deadline begins a transaction.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_NegativeWaitThrowsArgumentOutOfRangeException()
    {
        await using TempDirectory work = TempDirectory.Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(-2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromMilliseconds(-2)));
        await using ITransaction started = await global::Txfio.Txfio.BeginAsync(work.Path, Timeout.InfiniteTimeSpan);

        Assert.NotNull(started);
    }

    /// <summary>
    /// While waiting for a lock, the calling thread is not blocked.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has added a.txt, and the other has a 5-second wait.</para>
    /// <para>When: an Add of the same path is called, and the elapsed time and state are checked without awaiting the returned Task. Then the first transaction is disposed.</para>
    /// <para>Then: the call returns in under one second, the Task is still waiting, and it succeeds after the other side is disposed.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DoesNotBlockCallingThreadWhileWaiting()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (holder)
        {
            await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("a.txt", held);
            await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

            long started = Environment.TickCount64;
            Task waiting = waiter.AddAsync("a.txt", content);
            long returned = Environment.TickCount64 - started;

            Assert.True(returned < 1000, "The call blocked for " + returned + " ms");
            Assert.False(waiting.IsCompleted);
            await holder.DisposeAsync();
            await waiting;
            Assert.Single(waiter.GetPendingChanges());
        }
    }
}
