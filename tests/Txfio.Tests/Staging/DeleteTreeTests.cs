using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DeleteTreeTests
{
    /// <summary>
    /// Dispose without commit keeps the tree.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after DeleteTree of a directory with a child file.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the directory and the child file remain.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_DisposeWithoutCommitKeepsTree()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        string child = System.IO.Path.Combine(dir, "a.txt");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(child, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteTreeAsync("tree");
        }

        Assert.True(Directory.Exists(dir));
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// DeleteTree is one journal entry, and nothing on disk is deleted yet.
    /// </summary>
    /// <remarks>
    /// <para>Given: a child directory and files exist.</para>
    /// <para>When: DeleteTreeAsync is called.</para>
    /// <para>Then: one pending DeleteTree, and everything on disk remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_IsOneDeleteTreeEvenWithChildren()
    {
        await using TempDirectory work = TempDirectory.Create();
        string nested = System.IO.Path.Combine(work.Path, "tree", "child");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.DeleteTree, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(work.Path, "tree"), pending.Path);
        Assert.True(File.Exists(System.IO.Path.Combine(nested, "a.txt")));
    }

    /// <summary>
    /// An empty directory can be scheduled with DeleteTree too.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists.</para>
    /// <para>When: DeleteTreeAsync is called.</para>
    /// <para>Then: one pending DeleteTree.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_SchedulesEmptyDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// DeleteTree of a file is not supported.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists.</para>
    /// <para>When: DeleteTreeAsync is called on the file.</para>
    /// <para>Then: UnsupportedOperationException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_FileThrowsUnsupportedOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "file");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.DeleteTreeAsync("a.txt"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A missing directory cannot be scheduled.
    /// </summary>
    /// <remarks>
    /// <para>Given: the target directory does not exist.</para>
    /// <para>When: DeleteTreeAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the target.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_MissingDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string missing = System.IO.Path.Combine(work.Path, "missing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.DeleteTreeAsync("missing"));
        Assert.Equal(missing, conflict.Path);
    }

    /// <summary>
    /// DeleteTree is not possible with an operation under the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file under it is deleted.</para>
    /// <para>When: DeleteTreeAsync is called on the parent.</para>
    /// <para>Then: InvalidOperationException, and the earlier Delete remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_OperationUnderDirectoryThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("tree/a.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("tree"));

        Assert.Equal(PendingChangeKind.Delete, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Operations under a DeleteTree are rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory is scheduled with DeleteTree.</para>
    /// <para>When: a file is added under it.</para>
    /// <para>Then: InvalidOperationException, and the DeleteTree remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_UnderDeleteTreeThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("tree/a.txt", content));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// DeleteTree of the destination of a directory Move becomes a DeleteTree of the source directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with contents is moved.</para>
    /// <para>When: DeleteTreeAsync is called on the destination.</para>
    /// <para>Then: one pending DeleteTree of the source directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_DirectoryMoveDestinationBecomesSourceDeleteTree()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("tree", "other");

        await tx.DeleteTreeAsync("other");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.DeleteTree, pending.Kind);
        Assert.Equal(source, pending.Path);
    }

    /// <summary>
    /// Something under a directory Move cannot be deleted with DeleteTree.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory is moved.</para>
    /// <para>When: DeleteTreeAsync is called on a child directory of the source.</para>
    /// <para>Then: InvalidOperationException, and the Move remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_UnderDirectoryMoveThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree", "child"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("tree", "other");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("tree/child"));

        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
    }
}
