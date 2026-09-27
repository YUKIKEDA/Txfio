using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDeleteTests
{
    /// <summary>
    /// Committing a Delete deletes the target file and leaves no journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and neither the target nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeletedFileIsGone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing a Delete that canceled an Add creates nothing.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add followed by a Delete, so the pending changes are empty.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the target does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_CanceledAddCreatesNoFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Committing an Update made by an Add after a Delete replaces the content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Delete followed by an Add.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: the target content is the new one.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddAfterDeleteReplacesContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// If the Delete target is gone before commit, the result is Failed and the journal remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Delete, the target file is deleted externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the target does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenDeleteTargetIsGone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        File.Delete(target);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
