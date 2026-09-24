using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class ProcessLockTests
{
    /// <summary>
    /// 別プロセスが同じパスを押さえていると競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: 子プロセスが a.txt を Add してロックを持っている</para>
    /// <para>手順: 親プロセスが同じパスを Add する</para>
    /// <para>期待: LockContentionException になり、Path は a.txt の絶対パスである</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_別プロセスが同じパスを押さえるとLockContentionExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string ready = System.IO.Path.Combine(work.Path, "ready.signal");
        string stop = System.IO.Path.Combine(work.Path, "stop.signal");
        await using LockProcess child = LockProcess.StartHoldUntilStop(work.Path, "a.txt", ready, stop);
        await child.WaitUntilReadyAsync(ready);

        await using ITransaction parent = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => parent.AddAsync("a.txt", content));
        Assert.Equal(target, contention.Path);
        await child.StopAsync();
    }

    /// <summary>
    /// 別プロセスが別パスを押さえていても、こちらのパスはステージングできる
    /// </summary>
    /// <remarks>
    /// <para>前提: 子プロセスが b.txt を Add してロックを持っている</para>
    /// <para>手順: 親プロセスが a.txt を Add する</para>
    /// <para>期待: 両方ステージングされ、ロックファイルが 3 つある</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_別プロセスが別パスを押さえていてもステージングできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string ready = System.IO.Path.Combine(work.Path, "ready.signal");
        string stop = System.IO.Path.Combine(work.Path, "stop.signal");
        await using LockProcess child = LockProcess.StartHoldUntilStop(work.Path, "b.txt", ready, stop);
        await using ITransaction parent = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");
        await parent.AddAsync("a.txt", content);
        await child.WaitUntilReadyAsync(ready);

        string lockDirectory = System.IO.Path.GetDirectoryName(
            PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt")))!;
        Assert.Equal(3, Directory.GetFiles(lockDirectory, "*.lock").Length);
        Assert.Single(parent.GetPendingChanges());
        await child.StopAsync();
    }

    /// <summary>
    /// Dispose せず終了した別プロセスのロックは、Recover のあとで取り直せる
    /// </summary>
    /// <remarks>
    /// <para>前提: 子プロセスが a.txt を Add してロックを持っている</para>
    /// <para>手順: 子プロセスを Dispose せず終了し、親プロセスが BeginAsync する。RecoverAsync してから同じパスを Add する</para>
    /// <para>期待: Recover 前の BeginAsync は RecoveryRequiredException で、Recover のあとは Add でき、ロックファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_別プロセスがDisposeせず終了したあとは同じパスを押さえられること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        string ready = System.IO.Path.Combine(work.Path, "ready.signal");
        await using LockProcess child = LockProcess.StartHoldUntilKilled(work.Path, "a.txt", ready);
        await child.WaitUntilReadyAsync(ready);
        Assert.True(File.Exists(lockFile));
        await child.KillAsync();

        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path));
        Assert.Equal(work.Path, required.Path);
        Assert.Equal(RecoverResult.RolledBack, await global::Txfio.Txfio.RecoverAsync(work.Path));

        await using ITransaction parent = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");
        await parent.AddAsync("a.txt", content);
        Assert.True(File.Exists(lockFile));
        Assert.Single(parent.GetPendingChanges());
    }
}
