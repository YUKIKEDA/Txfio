using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLockTests
{
    /// <summary>
    /// Recover after a crash deletes the lock files, and another transaction can use the same path.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is stopped at AfterCommitting and disposed.</para>
    /// <para>When: after RecoverAsync, another transaction updates the same path.</para>
    /// <para>Then: RolledForward, the target has the Add content, the lock files are gone, and the Update works (the lock files are recreated).</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesLockFilesAfterAfterCommittingAndAnotherTransactionCanUsePath()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await TxFileIO.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.True(File.Exists(lockFile));
        Assert.False(File.Exists(target));

        RecoverReport result = await TxFileIO.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(lockFile));

        await using ITransaction next = await TxFileIO.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
        Assert.True(File.Exists(lockFile));
    }

    /// <summary>
    /// While another transaction holds a lock outside the orphan's footprint, Recover processes the orphan.
    /// </summary>
    /// <remarks>
    /// <para>Given: after holder begins, an Add of a.txt is stopped at AfterCommitting and disposed. holder has added b.txt.</para>
    /// <para>When: RecoverAsync runs, and holder calls CommitAsync.</para>
    /// <para>Then: Recover is RolledForward and a.txt has the stopped Add content. The commit succeeds and b.txt exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ProcessesOrphanWhileAnotherTransactionHoldsUnrelatedPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string held = System.IO.Path.Combine(work.Path, "b.txt");
        await using (ITransaction holder = await TxFileIO.BeginAsync(work.Path))
        {
            FaultInjector faults = new FaultInjector();
            faults.Arm(IFaultInjector.AfterCommitting);
            await using (ITransaction crashed = await TxFileIO.BeginAsync(work.Path, faults))
            {
                await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
                await crashed.AddAsync("a.txt", content);
                await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
            }

            await using MemoryStream heldContent = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("b.txt", heldContent);

            RecoverReport result = await TxFileIO.RecoverAsync(work.Path);
            Assert.Equal(RecoverResult.RolledForward, result.Result);
            Assert.Equal("staged", await File.ReadAllTextAsync(target));

            Assert.Equal(CommitResult.Succeeded, (await holder.CommitAsync()).Result);
            Assert.Equal("held", await File.ReadAllTextAsync(held));
        }
    }

    /// <summary>
    /// Recover deletes the path lock and intent lock files left by past operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub/a.txt is added and committed, and its path lock and intent lock files remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: the path lock and intent lock files of sub/a.txt are gone, and the only remaining .lock is the work-folder lock Recover had open.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesLeftoverLockFiles()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using (ITransaction tx = await TxFileIO.BeginAsync(work.Path))
        {
            await tx.WriteAllTextAsync("sub/a.txt", "a");
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        string file = System.IO.Path.Combine(work.Path, "sub", "a.txt");
        string pathLock = PathLockSet.FilePath(work.Path, file);
        string intentLock = PathLockSet.IntentFilePath(work.Path, System.IO.Path.Combine(work.Path, "sub"));
        string sentinel = PathLockSet.FilePath(work.Path, work.Path);
        Assert.True(File.Exists(pathLock));
        Assert.True(File.Exists(intentLock));

        await TxFileIO.RecoverAsync(work.Path);

        Assert.False(File.Exists(pathLock));
        Assert.False(File.Exists(intentLock));
        Assert.All(
            Directory.GetFiles(MetadataNames.LockFolderPath(work.Path), "*.lock"),
            path => Assert.Equal(sentinel, path, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// While a live transaction holds the shared work-folder lock, Recover skips the lock-file cleanup.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has added a.txt.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: NoPendingTransactions without an exception, and the lock file of a.txt remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_KeepsLockFilesWhileTransactionIsAlive()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await TxFileIO.BeginAsync(work.Path);
        await holder.WriteAllTextAsync("a.txt", "a");
        string lockFile = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt"));

        Assert.Equal(RecoverResult.NoPendingTransactions, (await TxFileIO.RecoverAsync(work.Path)).Result);

        Assert.True(File.Exists(lockFile));
    }
}
