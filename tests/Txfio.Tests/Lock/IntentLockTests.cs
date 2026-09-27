using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class IntentLockTests
{
    /// <summary>
    /// When what is under a directory is locked before the directory is reserved, the failed path is the directory being reserved.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory exists, and another transaction has added a file directly under it.</para>
    /// <para>When: DeleteTree is called on that directory.</para>
    /// <para>Then: LockContentionException, and Path is that directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_FailsWithDirectoryWhenChildLockedFirst()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        await holder.AddAsync("tree/a.txt", content);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => deleter.DeleteTreeAsync("tree"));

        Assert.Equal(tree, contention.Path);
        Assert.Empty(deleter.GetPendingChanges());
    }

    /// <summary>
    /// When what is under a reserved directory is touched, the failed path is the reserved directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory is scheduled with DeleteTree.</para>
    /// <para>When: another transaction adds a file directly under it.</para>
    /// <para>Then: LockContentionException, and Path is that directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_TouchingChildAfterReservationFailsWithDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await deleter.DeleteTreeAsync("tree");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("tree/a.txt", content));

        Assert.Equal(tree, contention.Path);
        Assert.Empty(child.GetPendingChanges());
    }

    /// <summary>
    /// DeleteTree can reserve even when another transaction holds an unrelated path.
    /// </summary>
    /// <remarks>
    /// <para>Given: another transaction has added a.txt, and tree exists.</para>
    /// <para>When: tree is scheduled with DeleteTree, then a third transaction adds a file under tree.</para>
    /// <para>Then: DeleteTree succeeds, and the Add under it throws LockContentionException with Path tree.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ReservesWhenUnrelatedPathIsHeld()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);

        await deleter.DeleteTreeAsync("tree");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        LockContentionException reserved = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("tree/b.txt", content));

        Assert.Equal(tree, reserved.Path);
        Assert.Single(deleter.GetPendingChanges());
    }

    /// <summary>
    /// The destination of a directory Move stays reserved after the call returns.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub exists.</para>
    /// <para>When: sub is moved to other, then another transaction adds a file under other.</para>
    /// <para>Then: LockContentionException, and Path is other.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DestinationStaysReservedAfterReturn()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dest = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await mover.MoveAsync("sub", "other");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("other/a.txt", content));

        Assert.Equal(dest, contention.Path);
    }

    /// <summary>
    /// The intent lock file is separate from the path lock of the same path.
    /// </summary>
    /// <remarks>
    /// <para>Given: a work folder and a path directly under it.</para>
    /// <para>When: the paths of the path lock and the intent lock are computed.</para>
    /// <para>Then: the two paths differ, and both end with .lock.</para>
    /// </remarks>
    [Fact]
    public void IntentFilePath_IsSeparateFromPathLock()
    {
        string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-intent");
        string target = System.IO.Path.Combine(work, "sub");

        string pathLock = PathLockSet.FilePath(work, target);
        string intent = PathLockSet.IntentFilePath(work, target);

        Assert.NotEqual(pathLock, intent);
        Assert.EndsWith(".lock", pathLock, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".lock", intent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A directory can be created even while another transaction has staged something.
    /// </summary>
    /// <remarks>
    /// <para>Given: another transaction has added a.txt.</para>
    /// <para>When: d is created with CreateDirectory.</para>
    /// <para>Then: d exists, and there is one pending change.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_CreatesWhileAnotherTransactionStaged()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction creator = await global::Txfio.Txfio.BeginAsync(work.Path);

        await creator.CreateDirectoryAsync("d");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "d")));
        Assert.Single(creator.GetPendingChanges());
    }

    /// <summary>
    /// A directory copy fails when another transaction has staged under the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: src/a.txt exists, and another transaction has added src/b.txt.</para>
    /// <para>When: src is copied to dest with CopyAsync.</para>
    /// <para>Then: LockContentionException with Path src, dest is not created, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FailsWhenSourceChildIsStaged()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("src/b.txt", held);
        await using ITransaction copier = await global::Txfio.Txfio.BeginAsync(work.Path);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => copier.CopyAsync("src", "dest"));

        Assert.Equal(source, contention.Path);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(copier.GetPendingChanges());
    }

    /// <summary>
    /// After a directory copy finishes, another transaction can stage under the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: src/a.txt exists.</para>
    /// <para>When: after src is copied to dest with CopyAsync, another transaction adds src/b.txt and dest/c.txt.</para>
    /// <para>Then: the Add of src/b.txt succeeds, and the Add of dest/c.txt throws LockContentionException with Path dest.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_OthersCanStageUnderSourceAfterCopy()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        await using ITransaction copier = await global::Txfio.Txfio.BeginAsync(work.Path);
        await copier.CopyAsync("src", "dest");
        await using ITransaction other = await global::Txfio.Txfio.BeginAsync(work.Path);

        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("b");
        await other.AddAsync("src/b.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("c");
        LockContentionException reserved = await Assert.ThrowsAsync<LockContentionException>(
            () => other.AddAsync("dest/c.txt", second));

        Assert.Equal(System.IO.Path.Combine(work.Path, "dest"), reserved.Path);
    }
}
