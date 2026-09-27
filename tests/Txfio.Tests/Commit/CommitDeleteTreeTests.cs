using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDeleteTreeTests
{
    /// <summary>
    /// Commit deletes the directory and everything under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: a tree with a child directory and files is scheduled with DeleteTree.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and neither the tree nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeletesEverythingUnderIt()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        string child = System.IO.Path.Combine(dir, "child", "a.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(child)!);
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Children added after scheduling are deleted at commit too.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an empty directory is scheduled with DeleteTree, a child file is created externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and neither the directory nor the child exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeletesChildrenAddedAfterScheduling()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        string child = System.IO.Path.Combine(dir, "later.txt");
        await File.WriteAllTextAsync(child, "later");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(child));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// If the directory becomes a file before commit, the result is Failed and that file remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a directory is scheduled with DeleteTree, the same path is made a file externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and that file remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenSwappedForFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "file");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.Equal("file", await File.ReadAllTextAsync(dir));
    }
}
