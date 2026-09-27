using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDirectoryDeleteTests
{
    /// <summary>
    /// Committing an empty directory deletes the target and leaves no journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory is deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and neither the directory nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_EmptyDirectoryIsGone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing Deletes of a child and its parent deletes both.
    /// </summary>
    /// <remarks>
    /// <para>Given: a direct child file and its parent directory are deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and neither the child nor the parent exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeletesChildAndParent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        string child = System.IO.Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub/a.txt");
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(child));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// Committing a Move out followed by a Delete of the parent moves the file and deletes the original directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a direct child file is moved out, and the parent is deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: the destination has the content, and the original directory is gone.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ParentIsGoneAfterMoveOut()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub/a.txt", "b.txt");
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Equal("moved", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// If a file is added directly under it externally before commit, the result is Failed and the directory remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an empty directory is deleted, a file is created directly under it externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenChildAddedExternally()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "external.txt"), "no");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.True(Directory.Exists(dir));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
