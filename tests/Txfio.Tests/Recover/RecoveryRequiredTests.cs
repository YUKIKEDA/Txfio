using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoveryRequiredTests
{
    /// <summary>
    /// While the leftovers of a crashed DeleteTree remain, only paths under its target are rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/old.txt exists, and a DeleteTree of d is stopped at AfterCommitting and disposed.</para>
    /// <para>When: a new transaction begins, adds d/new.txt, adds other.txt, and commits. Then RecoverAsync runs.</para>
    /// <para>Then: BeginAsync succeeds. The Add of d/new.txt throws RecoveryRequiredException with Path d/new.txt and stages nothing. The commit of other.txt succeeds. Recover is RolledForward and d is gone.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ThrowsRecoveryRequiredOnlyUnderCrashedDeleteTree()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "old.txt"), "old");
        await CrashDeleteTreeAsync(work.Path, "d");

        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream inside = LeftoverAddFiles.Utf8Stream("inside");
            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
                () => tx.AddAsync("d/new.txt", inside));
            Assert.Equal(System.IO.Path.Combine(tree, "new.txt"), required.Path);
            Assert.Empty(tx.GetPendingChanges());

            await using MemoryStream outside = LeftoverAddFiles.Utf8Stream("outside");
            await tx.AddAsync("other.txt", outside);
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        Assert.Equal("outside", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "other.txt")));
        Assert.True(Directory.Exists(tree));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(tree));
    }

    /// <summary>
    /// A transaction whose staging check passed while the owner was alive, and that took the lock the owner dropped when it crashed, cannot commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: tx1 has added a.txt. tx2, with a 5-second wait, starts adding a.txt and waits for tx1's path lock.</para>
    /// <para>When: tx1 is stopped at AfterCommitting and disposed, tx2's Add completes, and tx2 calls CommitAsync. Then tx2 is disposed and RecoverAsync runs.</para>
    /// <para>Then: the commit throws RecoveryRequiredException with Path a.txt and does not touch the disk. Recover is RolledForward, a.txt has tx1's content, and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ThrowsRecoveryRequiredWhenLockWasTakenFromCrashedOwner()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx2 = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5)))
        {
            Task adding;
            await using (ITransaction tx1 = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
            {
                await tx1.WriteAllTextAsync("a.txt", "first");
                adding = tx2.WriteAllTextAsync("a.txt", "second");
                await Task.Delay(200);
                Assert.False(adding.IsCompleted);
                await Assert.ThrowsAsync<CrashInjectionException>(() => tx1.CommitAsync());
            }

            await adding;
            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(() => tx2.CommitAsync());

            Assert.Equal(target, required.Path);
            Assert.False(File.Exists(target));
        }

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("first", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// A committing orphan rejects its own file path and the exclusive scheduling of its ancestor, but not a sibling.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/ exists and a journal with Committing has an Add of d/a.txt, without a liveness lock.</para>
    /// <para>When: a transaction adds d/a.txt, schedules DeleteTree of d, and adds d/b.txt.</para>
    /// <para>Then: the Add of d/a.txt throws with Path d/a.txt, the DeleteTree throws with Path d, and the Add of d/b.txt is staged.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_RejectsCommittingOrphanPathAndExclusiveAncestorButNotSibling()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(tree);
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, System.IO.Path.Combine("d", "a.txt"), "staged");

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream same = LeftoverAddFiles.Utf8Stream("same");
        RecoveryRequiredException onPath = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => tx.AddAsync("d/a.txt", same));
        RecoveryRequiredException onAncestor = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => tx.DeleteTreeAsync("d"));
        await using MemoryStream sibling = LeftoverAddFiles.Utf8Stream("sibling");
        await tx.AddAsync("d/b.txt", sibling);

        Assert.Equal(System.IO.Path.Combine(tree, "a.txt"), onPath.Path);
        Assert.Equal(tree, onAncestor.Path);
        PendingChange staged = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(System.IO.Path.Combine(tree, "b.txt"), staged.Path);
    }

    /// <summary>
    /// An uncommitted orphan's file paths are not in its footprint, so the same path can be staged and committed.
    /// </summary>
    /// <remarks>
    /// <para>Given: a journal without Committing has an Add of a.txt, without a liveness lock.</para>
    /// <para>When: a transaction adds a.txt and commits, then RecoverAsync runs.</para>
    /// <para>Then: the commit succeeds and a.txt has the new content. Recover is RolledBack, deletes only the orphan's .txnew, and a.txt keeps the new content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SucceedsOnFilePathOfUncommittedOrphan()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "a.txt", "staged");

        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.AddAsync("a.txt", content);
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("new", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// An uncommitted orphan still rejects paths under the directory it created.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction creates directory d and is disposed without rollback (as after a crash before Committing).</para>
    /// <para>When: another transaction adds d/a.txt, then adds e.txt.</para>
    /// <para>Then: the Add of d/a.txt throws with Path d/a.txt, and e.txt is staged.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ThrowsRecoveryRequiredUnderDirectoryCreatedByUncommittedOrphan()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await crashed.CreateDirectoryAsync("d");
            faults.SuppressRollback();
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream inside = LeftoverAddFiles.Utf8Stream("inside");
        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => tx.AddAsync("d/a.txt", inside));
        await using MemoryStream outside = LeftoverAddFiles.Utf8Stream("outside");
        await tx.AddAsync("e.txt", outside);

        Assert.Equal(System.IO.Path.Combine(work.Path, "d", "a.txt"), required.Path);
        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// A rejected call takes no lock, and the footprint the transaction kept is dropped once the journal is recovered.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/ exists and a journal with Committing has an Add of d/a.txt, without a liveness lock. A transaction has begun and staged nothing.</para>
    /// <para>When: the transaction adds d/a.txt, RecoverAsync runs while it is still open (rolling d/a.txt forward), and the transaction updates d/a.txt.</para>
    /// <para>Then: the first Add throws RecoveryRequiredException. RecoverAsync is RolledForward (it would report Contended if the rejected call had taken a lock on d/a.txt). The Update is staged.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_RejectedCallTakesNoLockAndKeptFootprintIsDroppedAfterRecover()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, System.IO.Path.Combine("d", "a.txt"), "staged");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await Assert.ThrowsAsync<RecoveryRequiredException>(() => tx.AddAsync("d/a.txt", first));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.UpdateAsync("d/a.txt", second);

        Assert.Single(tx.GetPendingChanges());
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
