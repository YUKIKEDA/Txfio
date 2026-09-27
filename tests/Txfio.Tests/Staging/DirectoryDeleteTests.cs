using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DirectoryDeleteTests
{
    /// <summary>
    /// Dispose without commit keeps the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after a Delete of an empty directory.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_DisposeWithoutCommitKeepsDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteAsync("sub");
        }

        Assert.True(Directory.Exists(dir));
    }

    /// <summary>
    /// It fails when there is an untracked file directly under the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file directly under the directory.</para>
    /// <para>When: DeleteAsync is called on the parent.</para>
    /// <para>Then: ExternalConflictException, and Path is the parent directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_UntrackedChildFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }

    /// <summary>
    /// It fails when there is an untracked directory directly under the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty child directory directly under the directory.</para>
    /// <para>When: DeleteAsync is called on the parent.</para>
    /// <para>Then: ExternalConflictException, and Path is the parent directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_UntrackedChildDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(System.IO.Path.Combine(dir, "nested"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }

    /// <summary>
    /// After a child file is deleted, the parent can be deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: a direct child file is deleted.</para>
    /// <para>When: DeleteAsync is called on the parent.</para>
    /// <para>Then: two pending Deletes, and everything on disk still remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_SchedulesParentAfterChild()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        string child = System.IO.Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub/a.txt");
        await tx.DeleteAsync("sub");

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(child));
    }

    /// <summary>
    /// After a Move out, the parent can be deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: a direct child file is moved out of the directory.</para>
    /// <para>When: DeleteAsync is called on the parent.</para>
    /// <para>Then: no exception, and the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_SchedulesParentAfterMoveOut()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub/a.txt", "b.txt");
        await tx.DeleteAsync("sub");

        Assert.True(Directory.Exists(dir));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// An Add into a directory scheduled for deletion fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory is deleted.</para>
    /// <para>When: AddAsync is called directly under it.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_IntoDirectoryScheduledForDeletionThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("sub/a.txt", content));
    }

    /// <summary>
    /// A Move into a directory being deleted fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory is deleted.</para>
    /// <para>When: MoveAsync targets a path directly under it.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_IntoDirectoryScheduledForDeletionThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "sub/a.txt"));
    }

    /// <summary>
    /// A Delete of the metadata folder fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: DeleteAsync is called on .txfio.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_MetadataFolderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync(".txfio"));
    }

    /// <summary>
    /// An Add under the metadata folder fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: AddAsync is called on a path under .txfio.</para>
    /// <para>Then: InvalidOperationException, and there is no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_UnderMetadataFolderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync(".txfio/foo", content));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// An Update under the metadata folder fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists under .txfio.</para>
    /// <para>When: UpdateAsync is called.</para>
    /// <para>Then: InvalidOperationException, and the file remains.</para>
    /// </remarks>
    [Fact]
    public async Task Update_UnderMetadataFolderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, ".txfio"));
        string inside = System.IO.Path.Combine(work.Path, ".txfio", "foo");
        await File.WriteAllTextAsync(inside, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync(".txfio/foo", content));
        Assert.Equal("keep", await File.ReadAllTextAsync(inside));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A Move to a path under the metadata folder fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists in the work folder.</para>
    /// <para>When: MoveAsync targets a path under .txfio.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_UnderMetadataFolderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", ".txfio/a.txt"));
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A Delete under the metadata folder fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists under .txfio.</para>
    /// <para>When: DeleteAsync is called on that path.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_UnderMetadataFolderThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, ".txfio"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, ".txfio", "foo"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync(".txfio/foo"));
    }

    /// <summary>
    /// A parent directory with a remaining Add cannot be deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file is added directly under it.</para>
    /// <para>When: DeleteAsync is called on the parent.</para>
    /// <para>Then: ExternalConflictException, and Path is the parent directory.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_ParentWithRemainingAddThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("sub/a.txt", content);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }
}
