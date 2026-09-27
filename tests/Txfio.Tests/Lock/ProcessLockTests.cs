using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class ProcessLockTests
{
    /// <summary>
    /// It conflicts when another process holds the same path.
    /// </summary>
    /// <remarks>
    /// <para>Given: a child process has added a.txt and holds the lock.</para>
    /// <para>When: the parent process adds the same path.</para>
    /// <para>Then: LockContentionException, and Path is the absolute path of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_SamePathHeldByAnotherProcessThrowsLockContentionException()
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
    /// Even when another process holds a different path, this path can be staged.
    /// </summary>
    /// <remarks>
    /// <para>Given: a child process has added b.txt and holds the lock.</para>
    /// <para>When: the parent process adds a.txt.</para>
    /// <para>Then: both are staged, and there are three lock files.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_StagesWhenAnotherProcessHoldsDifferentPath()
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
    /// The lock of another process that exited without Dispose can be taken again after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: a child process has added a.txt and holds the lock.</para>
    /// <para>When: the child process exits without Dispose, and the parent process calls BeginAsync. Then RecoverAsync runs and the same path is added.</para>
    /// <para>Then: BeginAsync before Recover throws RecoveryRequiredException, the Add works after Recover, and the lock file remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_TakesSamePathAfterAnotherProcessExitsWithoutDispose()
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
        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);

        await using ITransaction parent = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");
        await parent.AddAsync("a.txt", content);
        Assert.True(File.Exists(lockFile));
        Assert.Single(parent.GetPendingChanges());
    }
}
