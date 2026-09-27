using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class GetEntriesTests
{
    /// <summary>
    /// The list of direct children returns the post-commit view.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt, b.txt, and a directory d exist.</para>
    /// <para>When: a.txt is deleted, c.txt is added, and b.txt is moved to e.txt, then the direct children of the work folder itself are listed.</para>
    /// <para>Then: three entries, c.txt, d, and e.txt, and only d is a directory.</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_ReturnsDirectChildrenInPostCommitView()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "b");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await tx.WriteAllTextAsync("c.txt", "c");
        await tx.MoveAsync("b.txt", "e.txt");

        IReadOnlyList<DirectoryEntry> entries = await tx.GetEntriesAsync(".");

        Assert.Equal(new[] { "c.txt", "d", "e.txt" }, entries.Select(entry => System.IO.Path.GetFileName(entry.Path)).ToArray());
        Assert.Equal(new[] { false, true, false }, entries.Select(entry => entry.IsDirectory).ToArray());
    }

    /// <summary>
    /// The destination of a directory Move returns the source's contents, and the source does not exist.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/x.txt exists.</para>
    /// <para>When: Move(d→e), then e and d are listed.</para>
    /// <para>Then: e has one entry, e/x.txt, and d throws ExternalConflictException.</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_DirectoryMoveDestinationReturnsSourceContents()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "d", "x.txt"), "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("d", "e");

        IReadOnlyList<DirectoryEntry> entries = await tx.GetEntriesAsync("e");

        Assert.Equal(System.IO.Path.Combine(work.Path, "e", "x.txt"), Assert.Single(entries).Path);
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.GetEntriesAsync("d"));
    }

    /// <summary>
    /// A file, and the target of a DeleteTree, cannot be listed.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and t/x.txt exist.</para>
    /// <para>When: t is scheduled with DeleteTree, then a.txt and t are listed.</para>
    /// <para>Then: a.txt throws UnsupportedOperationException, and t throws ExternalConflictException.</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_CannotListFileOrDeletedDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "t"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "t", "x.txt"), "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("t");

        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.GetEntriesAsync("a.txt"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.GetEntriesAsync("t"));
    }
}
