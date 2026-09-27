using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitMoveTests
{
    /// <summary>
    /// Committing a Move moves the file and leaves no journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is moved.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, the destination has the content, and neither the source nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MovedFileGoesToDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing a Move that carried an Add creates only the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add followed by a Move.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, only the destination exists, and the source does not.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveAfterAddCreatesOnlyDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// Committing a Move after an Update replaces the destination content and deletes the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Update followed by a Move.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: the destination has the new content, and the source does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveAfterUpdateGivesDestinationNewContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
    }

    /// <summary>
    /// Committing a folded Move moves from the start to the end.
    /// </summary>
    /// <remarks>
    /// <para>Given: Move(A→B) followed by Move(B→C).</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: C has the content, and neither A nor B exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FoldedMoveGoesToEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("b.txt", "c.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// Committing an Update of the destination after a Move replaces the destination content and deletes the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: Move(A→B) followed by an Update of B.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: the destination has the new content, and the source does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateAfterMoveGivesDestinationNewContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Committing a Delete of the destination after a Move deletes the source and does not create the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: Move(A→B) followed by a Delete of B.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: neither the source nor the destination exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteDestinationAfterMoveDeletesSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Committing an Update and then a Delete of the Move destination deletes the source and does not create the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: Move(A→B), then an Update of B, then a Delete of B.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: neither the source nor the destination exists, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateThenDeleteMoveDestinationDeletesSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);
        await tx.DeleteAsync("b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing a Delete of the source after a Move deletes the source and does not create the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: Move(A→B) followed by a Delete of A.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: neither the source nor the destination exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteSourceAfterMoveDeletesSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// If the source is gone before commit, the result is Failed and the journal remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Move, the source is deleted externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenSourceIsGone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        File.Delete(source);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// If the destination appears before commit, the result is Failed and the journal remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Move, the destination is created externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenDestinationAppears()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(dest, "external");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Equal("external", await File.ReadAllTextAsync(dest));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// After an Update and a Move, an Add to the source does not change the destination content.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub/a.txt is updated and moved to a.txt.</para>
    /// <para>When: AddAsync to sub/a.txt, ReadAsync of a.txt, then CommitAsync.</para>
    /// <para>Then: the read returns the Update content; after Succeeded, a.txt has the Update content and sub/a.txt has the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddToSourceAfterUpdateAndMoveKeepsDestinationContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        string source = System.IO.Path.Combine(work.Path, "sub", "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "init");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("sub/a.txt", updated);
        await tx.MoveAsync("sub/a.txt", "a.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("sub/a.txt", added);

        Assert.Equal("updated", await tx.ReadAllTextAsync("a.txt"));
        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("updated", await File.ReadAllTextAsync(dest));
        Assert.Equal("added", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// After an Add and a Move, another Add to the source does not change the destination content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added and moved to b.txt.</para>
    /// <para>When: AddAsync to a.txt, then CommitAsync.</para>
    /// <para>Then: Succeeded, b.txt has the first Add content, and a.txt has the second Add content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddToSourceAfterAddAndMoveKeepsDestinationContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.AddAsync("a.txt", second);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("first", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Moving another file into a path whose Add was moved out, and updating it, does not change the content moved out earlier.
    /// </summary>
    /// <remarks>
    /// <para>Given: d.txt is added and moved to e.txt, and the existing a.txt is moved to d.txt.</para>
    /// <para>When: UpdateAsync on d.txt, then CommitAsync.</para>
    /// <para>Then: Succeeded, e.txt has the Add content, d.txt has the Update content, and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveIntoPathAndUpdateKeepsContentMovedOutEarlier()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("d.txt", added);
        await tx.MoveAsync("d.txt", "e.txt");
        await tx.MoveAsync("a.txt", "d.txt");
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("d.txt", updated);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "e.txt")));
        Assert.Equal("updated", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "d.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }
}
