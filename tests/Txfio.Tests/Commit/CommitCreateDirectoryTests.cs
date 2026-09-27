using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitCreateDirectoryTests
{
    /// <summary>
    /// Commit keeps the directory and the contents written with the plain file API.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, a child file is written with the plain file API.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, the directory and the child remain, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_KeepsDirectoryAndContents()
    {
        await using TempDirectory work = TempDirectory.Create();
        string child = System.IO.Path.Combine(work.Path, "drop", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(child, "keep");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// If the directory becomes a file before commit, the result is Failed and that file remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, the same path is made a file.</para>
    /// <para>When: CommitAsync runs, then the transaction is discarded.</para>
    /// <para>Then: Failed, and that file remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenSwappedForFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            Directory.Delete(dir);
            await File.WriteAllTextAsync(dir, "file");

            CommitReport result = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, result.Result);
        }

        Assert.Equal("file", await File.ReadAllTextAsync(dir));
    }

    /// <summary>
    /// Even when another operation fails the check, discard deletes the created directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a CreateDirectory, and an Update of a file that was deleted before commit.</para>
    /// <para>When: CommitAsync runs, then the transaction is discarded.</para>
    /// <para>Then: Failed, and drop and the file inside it are gone.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DiscardAfterFailedCheckDeletesDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string child = System.IO.Path.Combine(work.Path, "drop", "b.txt");
        await File.WriteAllTextAsync(updated, "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            await File.WriteAllTextAsync(child, "gone");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("a.txt", content);
            File.Delete(updated);

            CommitReport result = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, result.Result);
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.False(File.Exists(child));
    }

    /// <summary>
    /// An Add under it stays at the real path after commit, and so does the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory, and drop/a.txt is added.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, a.txt remains, and there is no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_KeepsAddUnderDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string child = System.IO.Path.Combine(work.Path, "drop", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await tx.WriteAllTextAsync("drop/a.txt", "staged");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(child));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "drop"), "*.txnew"));
    }
}
