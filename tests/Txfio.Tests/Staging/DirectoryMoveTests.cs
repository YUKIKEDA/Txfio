using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DirectoryMoveTests
{
    /// <summary>
    /// A directory with contents moves at commit, and does not move before commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub contains a.txt and nested/b.txt.</para>
    /// <para>When: it is moved to other and committed.</para>
    /// <para>Then: before commit it is still sub; after commit other has the same content and sub does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_CommitMovesDirectoryAndContents()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        string nested = System.IO.Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "hello");
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "b.txt"), "deep");
        string dest = System.IO.Path.Combine(work.Path, "other");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");

        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(dest));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(dest, "a.txt")));
        Assert.Equal("deep", await File.ReadAllTextAsync(System.IO.Path.Combine(dest, "nested", "b.txt")));
    }

    /// <summary>
    /// Dispose without commit does not move the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty sub exists.</para>
    /// <para>When: it is moved and the transaction is disposed.</para>
    /// <para>Then: sub remains, and other does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DisposeWithoutCommitKeepsDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("sub", "other");
        }

        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
    }

    /// <summary>
    /// An occupied destination throws ExternalConflictException.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub exists, other is a file, and taken is a directory.</para>
    /// <para>When: sub is moved to each.</para>
    /// <para>Then: both throw ExternalConflictException with Path set to the destination, and sub remains.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OccupiedDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        string file = System.IO.Path.Combine(work.Path, "other");
        await File.WriteAllTextAsync(file, "keep");
        string directory = System.IO.Path.Combine(work.Path, "taken");
        Directory.CreateDirectory(directory);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException fileConflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.MoveAsync("sub", "other"));
        ExternalConflictException directoryConflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.MoveAsync("sub", "taken"));

        Assert.Equal(file, fileConflict.Path);
        Assert.Equal(directory, directoryConflict.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
    }

    /// <summary>
    /// A directory Move that differs only in case fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty sub exists.</para>
    /// <para>When: it is moved to Sub, then from sub to sub.</para>
    /// <para>Then: both throw InvalidOperationException, there are no pending changes and no locks, and sub remains.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DirectoryDifferingOnlyInCaseThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException differentCase = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.MoveAsync("sub", "Sub"));
        InvalidOperationException same = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.MoveAsync("sub", "sub"));

        Assert.Contains("A path cannot be moved to itself", differentCase.Message, StringComparison.Ordinal);
        Assert.Contains("A path cannot be moved to itself", same.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        Assert.True(Directory.Exists(source));
    }

    /// <summary>
    /// A directory cannot be moved under itself, and the move fails when there is an operation under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub contains a.txt.</para>
    /// <para>When: sub is moved to sub/inner, and after a.txt is added, sub is moved.</para>
    /// <para>Then: both throw InvalidOperationException, the first Move takes no lock, and afterwards the pending change stays the Add.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_UnderItselfOrWithOperationsUnderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("sub", "sub/inner"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        await using MemoryStream content = new MemoryStream("new"u8.ToArray());
        await tx.AddAsync("sub/b.txt", content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("sub", "other"));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Consecutive Moves fold from the start to the end.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty sub exists.</para>
    /// <para>When: sub is moved to mid, mid to final, and committed.</para>
    /// <para>Then: one pending change from sub to final, and after commit only final exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ConsecutiveMovesFoldFromStartToEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        string dest = System.IO.Path.Combine(work.Path, "final");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "mid");
        await tx.MoveAsync("mid", "final");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "mid")));
        Assert.True(Directory.Exists(dest));
    }

    /// <summary>
    /// A Delete after a Move of an empty directory becomes a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty sub is moved to other.</para>
    /// <para>When: other is deleted and committed.</para>
    /// <para>Then: the pending change is a Delete of sub, and after commit neither sub nor other exists.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_AfterEmptyDirectoryMoveBecomesSourceDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");
        await tx.DeleteAsync("other");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
    }

    /// <summary>
    /// A Delete after a Move of a directory with contents fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub with a.txt is moved to other.</para>
    /// <para>When: other is deleted.</para>
    /// <para>Then: ExternalConflictException, and the pending change stays the Move.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_AfterDirectoryMoveWithContentsThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("other"));

        Assert.Equal(source, conflict.Path);
        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Adds outside the tree can be scheduled after a directory Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty sub is moved.</para>
    /// <para>When: c.txt is added and committed.</para>
    /// <para>Then: two pending operations, and after commit other and c.txt exist.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_OutsideTreeAfterDirectoryMove()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");
        await using MemoryStream content = new MemoryStream("out"u8.ToArray());
        await tx.AddAsync("c.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
        Assert.Equal("out", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// A directory Move can be scheduled even when another transaction holds an unrelated path.
    /// </summary>
    /// <remarks>
    /// <para>Given: another transaction has added a.txt, and sub exists.</para>
    /// <para>When: sub is moved.</para>
    /// <para>Then: the Move succeeds, and there is one pending change.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_SchedulesDirectoryMoveWhenUnrelatedPathIsHeld()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = new MemoryStream("a"u8.ToArray());
        await holder.AddAsync("a.txt", content);
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);

        await mover.MoveAsync("sub", "other");

        Assert.Single(mover.GetPendingChanges());
    }

    /// <summary>
    /// When the share-lost marker is in use, the exclusive lock is given up and shared is restored.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work-folder lock is held exclusively, and the marker is open as shared.</para>
    /// <para>When: whether the marker is in use is checked.</para>
    /// <para>Then: LockContentionException with Path set to the work folder; after the check fails, another set can take shared, but not exclusive.</para>
    /// </remarks>
    [Fact]
    public async Task RejectForeignLocks_ReturnsToSharedWhenMarkerIsInUse()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        using FileStream held = new FileStream(
            MetadataNames.ShareLostLockPath(work.Path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        PathLockSet mover = new PathLockSet();
        await mover.AcquireExclusiveAsync(work.Path, default);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => mover.RejectForeignLocksAsync(work.Path, default));

        Assert.Equal(work.Path, contention.Path);
        PathLockSet other = new PathLockSet();
        await other.AcquireSharedAsync(work.Path, default);
        await Assert.ThrowsAsync<LockContentionException>(() => other.AcquireExclusiveAsync(work.Path, default));
        mover.Release();
        other.Release();
    }

    /// <summary>
    /// When shared cannot be restored while holding a path, the marker remains and blocks exclusive.
    /// </summary>
    /// <remarks>
    /// <para>Given: a path lock is held, and reopening as exclusive and returning to shared both fail with sharing violations.</para>
    /// <para>When: exclusive is taken, another set takes exclusive, the marker is checked, and the first set is disposed.</para>
    /// <para>Then: the check throws LockContentionException with Path set to the work folder, and after disposal the marker can be opened without sharing.</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_MarkerRemainsWhenSharedIsLostWhileHoldingPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        FaultInjector faults = new FaultInjector();
        PathLockSet holder = new PathLockSet(faults);
        await holder.AcquireSharedAsync(work.Path, default);
        await holder.AcquireAsync(work.Path, new[] { target }, default);
        faults.FailNextOpen(FileShare.None, SharingViolation());
        faults.FailNextOpen(FileShare.ReadWrite, SharingViolation());
        try
        {
            await Assert.ThrowsAsync<LockContentionException>(() => holder.AcquireExclusiveAsync(work.Path, default));
        }
        finally
        {
            faults.ClearOpenFailures();
        }

        string marker = MetadataNames.ShareLostLockPath(work.Path);
        Assert.True(File.Exists(marker));
        PathLockSet mover = new PathLockSet();
        await mover.AcquireExclusiveAsync(work.Path, default);
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => mover.RejectForeignLocksAsync(work.Path, default));
        Assert.Equal(work.Path, contention.Path);
        holder.Release();
        using (FileStream free = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = free;
        }

        mover.Release();
    }

    /// <summary>
    /// Even when raising to exclusive fails, shared is kept.
    /// </summary>
    /// <remarks>
    /// <para>Given: two sets hold the work-folder lock as shared.</para>
    /// <para>When: one tries to go exclusive. After that fails, the other is released, and a third takes exclusive.</para>
    /// <para>Then: the raise throws LockContentionException, and the third cannot take exclusive either.</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_KeepsSharedWhenRaiseFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        PathLockSet holder = new PathLockSet();
        PathLockSet mover = new PathLockSet();
        await holder.AcquireSharedAsync(work.Path, default);
        await mover.AcquireSharedAsync(work.Path, default);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => mover.AcquireExclusiveAsync(work.Path, default));

        Assert.Equal(work.Path, contention.Path);
        holder.Release();
        PathLockSet third = new PathLockSet();
        await Assert.ThrowsAsync<LockContentionException>(() => third.AcquireExclusiveAsync(work.Path, default));
        mover.Release();
        third.Release();
    }

    /// <summary>
    /// An IO failure that prevents returning to shared is not swallowed, and shared is reopened at the next acquisition.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work-folder lock is held as shared. The raise fails with a sharing violation, and the return with another IOException.</para>
    /// <para>When: exclusive is taken. After the failure is cleared, shared is taken again.</para>
    /// <para>Then: the IOException from the return propagates as is. After shared is taken again, another set cannot take exclusive.</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_ReopensSharedAtNextAcquisitionAfterReturnFailure()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        PathLockSet mover = new PathLockSet(faults);
        await mover.AcquireSharedAsync(work.Path, default);
        faults.FailNextOpen(FileShare.None, SharingViolation());
        faults.FailNextOpen(FileShare.ReadWrite, new IOException("disk"));
        try
        {
            IOException failure = await Assert.ThrowsAsync<IOException>(() => mover.AcquireExclusiveAsync(work.Path, default));
            Assert.Equal("disk", failure.Message);
        }
        finally
        {
            faults.ClearOpenFailures();
        }

        await mover.AcquireSharedAsync(work.Path, default);
        PathLockSet other = new PathLockSet();
        await Assert.ThrowsAsync<LockContentionException>(() => other.AcquireExclusiveAsync(work.Path, default));
        mover.Release();
        other.Release();
    }

    /// <summary>
    /// Exclusive after failing to return to shared reopens shared first.
    /// </summary>
    /// <remarks>
    /// <para>Given: both the raise and the return to shared have failed with sharing violations.</para>
    /// <para>When: the failure is cleared, and exclusive is retried while another set holds shared. That set is released, and a third takes exclusive.</para>
    /// <para>Then: the retry throws LockContentionException, and the third cannot take exclusive either.</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_RestoresSharedBeforeTryingExclusiveAfterLosingLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        PathLockSet mover = new PathLockSet(faults);
        await mover.AcquireSharedAsync(work.Path, default);
        faults.FailNextOpen(FileShare.None, SharingViolation());
        faults.FailNextOpen(FileShare.ReadWrite, SharingViolation());
        try
        {
            await Assert.ThrowsAsync<LockContentionException>(() => mover.AcquireExclusiveAsync(work.Path, default));
        }
        finally
        {
            faults.ClearOpenFailures();
        }

        PathLockSet holder = new PathLockSet();
        await holder.AcquireSharedAsync(work.Path, default);
        await Assert.ThrowsAsync<LockContentionException>(() => mover.AcquireExclusiveAsync(work.Path, default));
        holder.Release();
        PathLockSet third = new PathLockSet();
        await Assert.ThrowsAsync<LockContentionException>(() => third.AcquireExclusiveAsync(work.Path, default));
        mover.Release();
        third.Release();
    }

    /// <summary>
    /// A directory swap replaces the existing directory at the destination, with its contents.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt and build/new.txt exist.</para>
    /// <para>When: Move(build→site, overwrite: true) is scheduled, and the post-commit view and the disk are checked.</para>
    /// <para>Then: before commit site/old.txt remains; in the view site/new.txt exists and site/old.txt does not; after commit site has only new.txt, and neither build nor .txold exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OverwriteSwapsDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        string build = System.IO.Path.Combine(work.Path, "build");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(build, "new.txt"), "new");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("build", "site", overwrite: true);

        Assert.True(File.Exists(System.IO.Path.Combine(site, "old.txt")));
        Assert.Equal("new", await tx.ReadAllTextAsync("site/new.txt"));
        Assert.False(await tx.ExistsAsync("site/old.txt"));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(new[] { "new.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.False(Directory.Exists(build));
        Assert.Empty(Directory.GetDirectories(work.Path, "*.txold"));
    }

    /// <summary>
    /// A directory created by Import can swap out an existing directory in the same transaction.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt, and incoming/a.txt outside the work folder.</para>
    /// <para>When: incoming is imported to site.new with ImportAsync, then Move(site.new→site, overwrite: true), then commit.</para>
    /// <para>Then: after commit site has only a.txt, and site.new does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_SwapsWithDirectoryCreatedByImport()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        Directory.CreateDirectory(site);
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        string incoming = System.IO.Path.Combine(outside.Path, "incoming");
        Directory.CreateDirectory(incoming);
        await File.WriteAllTextAsync(System.IO.Path.Combine(incoming, "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportAsync(incoming, "site.new");
        await tx.MoveAsync("site.new", "site", overwrite: true);

        Assert.Equal("alpha", await tx.ReadAllTextAsync("site/a.txt"));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(new[] { "a.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.Equal("alpha", await File.ReadAllTextAsync(System.IO.Path.Combine(site, "a.txt")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "site.new")));
    }

    /// <summary>
    /// A DeleteTree at the destination folds into the directory swap, and no further operation is allowed under the swap.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt and build/new.txt exist.</para>
    /// <para>When: DeleteTree(site), then Move(build→site, overwrite: true), then a write to site/x.txt.</para>
    /// <para>Then: the operations are one Move, and the write throws InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_FoldsDestinationDeleteTreeIntoSwap()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "site"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "site", "old.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.DeleteTreeAsync("site");
        await tx.MoveAsync("build", "site", overwrite: true);

        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("site/x.txt", "x"));
    }

    /// <summary>
    /// With an operation under a source that is not a created directory, the swap is refused.
    /// </summary>
    /// <remarks>
    /// <para>Given: site and build exist, and build/a.txt is added.</para>
    /// <para>When: Move(build→site, overwrite: true) is called.</para>
    /// <para>Then: InvalidOperationException, and the operations stay one Add.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_RefusesSwapWithOperationsUnderNonCreatedSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "site"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("build/a.txt", "a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("build", "site", overwrite: true));

        Assert.Single(tx.GetPendingChanges());
    }

    private static IOException SharingViolation()
    {
        IOException exception = new IOException("sharing");
        exception.HResult = unchecked((int)0x80070020);
        return exception;
    }
}
