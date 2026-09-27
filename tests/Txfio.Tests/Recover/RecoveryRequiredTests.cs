using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoveryRequiredTests
{
    /// <summary>
    /// While the leftovers of a crashed DeleteTree remain, a new transaction cannot begin.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/old.txt exists, and a DeleteTree of d is stopped at AfterCommitting and disposed.</para>
    /// <para>When: BeginAsync, then RecoverAsync, then BeginAsync again.</para>
    /// <para>Then: the first throws RecoveryRequiredException (Path is the work folder) and d remains. Recover is RolledForward and d is gone, and the second begins.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_ThrowsRecoveryRequiredWhileCrashedDeleteTreeRemains()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "old.txt"), "old");
        await CrashDeleteTreeAsync(work.Path, "d");

        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Equal(work.Path, required.Path);
        Assert.True(Directory.Exists(tree));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(tree));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }

    /// <summary>
    /// If another transaction crashes after this one began, the commit is rejected without touching the disk.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/old.txt exists. After tx2 begins, tx1 stops a DeleteTree of d at AfterCommitting and is disposed.</para>
    /// <para>When: tx2 adds d/important.txt and calls CommitAsync, is disposed, and RecoverAsync runs.</para>
    /// <para>Then: the commit throws RecoveryRequiredException and d/important.txt is not created. Recover is RolledForward, d is gone, and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ThrowsRecoveryRequiredWithoutTouchingDiskWhenAnotherTransactionCrashesAfterBegin()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        string important = System.IO.Path.Combine(tree, "important.txt");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "old.txt"), "old");
        await using (ITransaction tx2 = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await CrashDeleteTreeAsync(work.Path, "d");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("important");
            await tx2.AddAsync(@"d/important.txt", content);

            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(() => tx2.CommitAsync());

            Assert.Equal(work.Path, required.Path);
            Assert.False(File.Exists(important));
            Assert.True(File.Exists(System.IO.Path.Combine(tree, "old.txt")));
        }

        Assert.Empty(Directory.GetFiles(tree, "*.txnew"));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(tree));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// An uncommitted orphaned journal also rejects begin, and begin works after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: only an uncommitted journal without a liveness lock exists.</para>
    /// <para>When: BeginAsync, then RecoverAsync, then BeginAsync again.</para>
    /// <para>Then: the first throws RecoveryRequiredException and creates neither a new journal nor a liveness lock. Recover is RolledBack, and the second begins.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_ThrowsRecoveryRequiredWithUncommittedOrphanedJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        await Assert.ThrowsAsync<RecoveryRequiredException>(() => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }

    /// <summary>
    /// A commit without operations does not check for orphaned journals.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a transaction begins, an uncommitted orphaned journal appears.</para>
    /// <para>When: CommitAsync runs without any operation.</para>
    /// <para>Then: Succeeded, and the orphaned journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SucceedsWithoutOperationsEvenWithOrphanedJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    private static async Task CrashDeleteTreeAsync(string workFolder, string path)
    {
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using ITransaction crashed = await global::Txfio.Txfio.BeginAsync(workFolder, faults);
        await crashed.DeleteTreeAsync(path);
        await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
    }
}
