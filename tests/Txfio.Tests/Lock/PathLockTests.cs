using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class PathLockTests
{
    /// <summary>
    /// Another transaction cannot lock the same path.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has an Add.</para>
    /// <para>When: the other adds the same path, and the same path with different case.</para>
    /// <para>Then: both throw LockContentionException, and Path is each Add target.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_SamePathInAnotherTransactionThrowsLockContentionException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string lower = System.IO.Path.Combine(work.Path, "a.txt");
        string upper = System.IO.Path.Combine(work.Path, "A.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException same = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("a.txt", again));
        Assert.Equal(lower, same.Path);

        await using MemoryStream cased = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException differentCase = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("A.txt", cased));
        Assert.Equal(upper, differentCase.Path);
    }

    /// <summary>
    /// Different paths can be staged at the same time.
    /// </summary>
    /// <remarks>
    /// <para>Given: two transactions have begun.</para>
    /// <para>When: each adds a different path.</para>
    /// <para>Then: both are staged, and there are three lock files.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DifferentPathsStageConcurrently()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream left = LeftoverAddFiles.Utf8Stream("a");
        await using MemoryStream right = LeftoverAddFiles.Utf8Stream("b");
        await first.AddAsync("a.txt", left);
        await second.AddAsync("b.txt", right);

        string lockDirectory = System.IO.Path.GetDirectoryName(
            PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt")))!;
        Assert.Equal(3, Directory.GetFiles(lockDirectory, "*.lock").Length);
        Assert.Single(first.GetPendingChanges());
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// A restage in the same transaction does not conflict.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: the same transaction updates it again.</para>
    /// <para>Then: no exception, and the lock files stay two: the work-folder lock and the target.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_RestageInSameTransactionDoesNotConflict()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("one");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("two");
        await tx.UpdateAsync("a.txt", first);
        await tx.UpdateAsync("a.txt", second);

        Assert.Equal(2, Directory.GetFiles(System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, target))!, "*.lock").Length);
        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// After commit, another transaction can lock the path, and the .lock remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add has been committed.</para>
    /// <para>When: another transaction updates the same path.</para>
    /// <para>Then: the Update works, and the .lock file remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AnotherTransactionCanLockSamePathAfterwardAndFileRemains()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        Assert.True(File.Exists(lockFile));
        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
    }

    /// <summary>
    /// After Dispose, another transaction can lock the path, and the .lock remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction with an Add has been disposed.</para>
    /// <para>When: another transaction adds the same path.</para>
    /// <para>Then: the Add works, and the .lock file remains.</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_AnotherTransactionCanLockSamePathAfterwardAndFileRemains()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);
        await first.DisposeAsync();

        Assert.True(File.Exists(lockFile));
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        await second.AddAsync("a.txt", again);
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// Even if deleting the journal fails during rollback, the locks are closed and no exception is thrown.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, the journal is opened without sharing.</para>
    /// <para>When: DisposeAsync, then BeginAsync; the journal is closed, RecoverAsync runs, and another transaction adds the same path.</para>
    /// <para>Then: DisposeAsync throws nothing, the .txnew is gone, BeginAsync throws RecoveryRequiredException (Path is the work folder), Recover is RolledBack, and the Add works afterwards.</para>
    /// </remarks>
    [WindowsFact("An open file cannot be deleted")]
    public async Task DisposeAsync_ClosesLocksWhenJournalDeleteFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        using (FileStream hold = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(hold.CanRead);
            await first.DisposeAsync();
            Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));

            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
                () => global::Txfio.Txfio.BeginAsync(work.Path));
            Assert.Equal(work.Path, required.Path);
        }

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);

        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        await second.AddAsync("a.txt", again);
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// Even after an Add without a parent, the path stays held.
    /// </summary>
    /// <remarks>
    /// <para>Given: the parent directory does not exist.</para>
    /// <para>When: an Add is made, then another transaction adds the same path.</para>
    /// <para>Then: the first throws ExternalConflictException, and the second throws LockContentionException.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_LockRemainsWithoutParent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "missing", "a.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<ExternalConflictException>(() => first.AddAsync("missing/a.txt", content));

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("missing/a.txt", again));
        Assert.Equal(target, contention.Path);
    }

    /// <summary>
    /// Move locks both the source and the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: the source file exists.</para>
    /// <para>When: after the Move, another transaction deletes the source and adds the destination.</para>
    /// <para>Then: both throw LockContentionException, and Path is each target.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_LocksBothSourceAndDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "src");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.MoveAsync("a.txt", "b.txt");

        LockContentionException sourceLock = await Assert.ThrowsAsync<LockContentionException>(() => second.DeleteAsync("a.txt"));
        Assert.Equal(source, sourceLock.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        LockContentionException destLock = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("b.txt", content));
        Assert.Equal(dest, destLock.Path);
    }

    /// <summary>
    /// Even if the second lock of a Move cannot be taken, the first remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: another transaction has updated the destination.</para>
    /// <para>When: a Move to that destination is made, then a third transaction deletes the source.</para>
    /// <para>Then: the Move throws LockContentionException on the destination, and the source's lock still cannot be taken.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_FirstLockRemainsWhenSecondFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "held.txt");
        await File.WriteAllTextAsync(source, "src");
        await File.WriteAllTextAsync(dest, "held");
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction other = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await holder.UpdateAsync("held.txt", content);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => mover.MoveAsync("a.txt", "held.txt"));
        Assert.Equal(dest, contention.Path);
        LockContentionException sourceLock = await Assert.ThrowsAsync<LockContentionException>(() => other.DeleteAsync("a.txt"));
        Assert.Equal(source, sourceLock.Path);
    }

    /// <summary>
    /// A directory Delete stops staging under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory is scheduled for Delete.</para>
    /// <para>When: another transaction adds a file directly under it.</para>
    /// <para>Then: LockContentionException, and Path is that directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_OtherTransactionsCannotTouchUnderDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string sub = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(sub);
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.DeleteAsync("sub");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("sub/a.txt", content));

        Assert.Equal(sub, contention.Path);
        Assert.Empty(second.GetPendingChanges());
    }

    /// <summary>
    /// After a directory Move, other transactions cannot touch the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory exists.</para>
    /// <para>When: it is moved, then another transaction deletes the source.</para>
    /// <para>Then: LockContentionException, and Path is the source directory.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DirectorySourceStaysReservedAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        string sub = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(sub);
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.MoveAsync("sub", "other");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => second.DeleteAsync("sub"));

        Assert.Equal(sub, contention.Path);
        Assert.Equal(PendingChangeKind.Move, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// After DeleteTree, unrelated paths pass and what is under it is blocked.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory and another file exist.</para>
    /// <para>When: DeleteTree, then another transaction deletes the other file and adds a file under the directory.</para>
    /// <para>Then: the other file can be deleted, and the Add under it throws LockContentionException with Path set to that directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ReservesOnlyUnderDirectoryAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.DeleteTreeAsync("tree");

        await second.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("tree/a.txt", content));

        Assert.Equal(tree, contention.Path);
        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// After CreateDirectory, other transactions can touch unrelated paths and what is under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: another file a.txt exists.</para>
    /// <para>When: CreateDirectory, then another transaction deletes a.txt and adds a file directly under the created directory.</para>
    /// <para>Then: both succeed.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_DoesNotReserveUnderDirectoryAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateDirectoryAsync("drop");

        await second.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        await second.AddAsync("drop/a.txt", content);

        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(2, second.GetPendingChanges().Count);
    }

    /// <summary>
    /// Moves in opposite directions called at the same time do not hang.
    /// </summary>
    /// <remarks>
    /// <para>Given: a source a.txt exists, and b.txt does not.</para>
    /// <para>When: Move(a→b) and Move(b→a) start at the same time, and both are awaited.</para>
    /// <para>Then: they finish within 5 seconds, and one throws LockContentionException.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OppositeMovesAtSameTimeDoNotHang()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        Task forward = first.MoveAsync("a.txt", "b.txt");
        Task backward = second.MoveAsync("b.txt", "a.txt");
        Task both = Task.WhenAll(forward, backward);
        Task finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(both, finished);

        int contentions = 0;
        if (forward.IsFaulted && forward.Exception!.InnerException is LockContentionException)
        {
            contentions++;
        }

        if (backward.IsFaulted && backward.Exception!.InnerException is LockContentionException)
        {
            contentions++;
        }

        Assert.Equal(1, contentions);
    }

    /// <summary>
    /// A file copy does not block changes to other paths.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: a.txt is copied to b.txt, then another transaction adds c.txt.</para>
    /// <para>Then: the Add succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FileDoesNotBlockOtherPaths()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CopyAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("other");

        await second.AddAsync("c.txt", content);

        Assert.Equal(PendingChangeKind.Add, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A directory copy reserves only the destination, and lets the source and unrelated paths pass.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a child file, and another file, exist.</para>
    /// <para>When: the directory is copied, then another transaction deletes the other file and adds files under the source and under the destination.</para>
    /// <para>Then: the other file and the source child succeed, and the destination child throws LockContentionException with Path set to the destination.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DirectoryReservesOnlyDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string dest = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CopyAsync("src", "dest");

        await second.DeleteAsync("a.txt");
        await using MemoryStream sourceChild = LeftoverAddFiles.Utf8Stream("more");
        await second.AddAsync("src/more.txt", sourceChild);
        await using MemoryStream destChild = LeftoverAddFiles.Utf8Stream("no");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("dest/more.txt", destChild));

        Assert.Equal(dest, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(2, second.GetPendingChanges().Count);
    }

    /// <summary>
    /// Creating a ZIP from a directory does not reserve what is under it after the call returns.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a child file, and another file, exist.</para>
    /// <para>When: a ZIP is created from the directory, then another transaction deletes that file.</para>
    /// <para>Then: the Delete succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_DirectoryDoesNotReserveUnderItAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync("src", "src.zip");

        await second.DeleteAsync("a.txt");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Extracting a ZIP reserves the destination, and lets unrelated paths pass.
    /// </summary>
    /// <remarks>
    /// <para>Given: a ZIP and another file exist.</para>
    /// <para>When: the ZIP is extracted, then another transaction deletes the other file and adds a file under the destination.</para>
    /// <para>Then: the other file can be deleted, and the Add under the destination throws LockContentionException with Path set to the destination.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_ReservesOnlyDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string dest = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, System.IO.Path.Combine(work.Path, "src.zip"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ExtractArchiveAsync("src.zip", "dest");

        await second.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("dest/more.txt", content));

        Assert.Equal(dest, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Creating a ZIP from a list of files only does not block changes to other paths.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist.</para>
    /// <para>When: a ZIP is created from a list with only a.txt, then another transaction deletes b.txt.</para>
    /// <para>Then: the Delete succeeds, and each transaction has one operation.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_FileOnlyListDoesNotBlockOtherPaths()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt") }, "a.zip");

        await second.DeleteAsync("b.txt");

        Assert.Single(first.GetPendingChanges());
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// Creating a ZIP from a list that includes a directory does not block other paths after the call returns.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a child file, and two other files, exist.</para>
    /// <para>When: a ZIP is created from a list with one file and the directory, then another transaction deletes the other file.</para>
    /// <para>Then: the Delete succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ListWithDirectoryDoesNotBlockOtherPathsAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt"), new ArchiveEntrySource("src") }, "all.zip");

        await second.DeleteAsync("b.txt");

        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }
}
