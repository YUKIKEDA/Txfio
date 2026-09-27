using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitMoveChainTests
{
    /// <summary>
    /// Moving aside and then replacing keeps the original content at the backup and puts the new content at the original path.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: Move(a→a.bak), then Add to a.txt, then CommitAsync.</para>
    /// <para>Then: Succeeded, a.bak has the old content, and a.txt has the new content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveAsideAndReplaceKeepsBoth()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.bak")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Log rotation is applied from the free end.
    /// </summary>
    /// <remarks>
    /// <para>Given: log.txt and log.1 exist, and log.2 does not.</para>
    /// <para>When: Move(log.1→log.2), Move(log→log.1), Add to log.txt, then CommitAsync.</para>
    /// <para>Then: Succeeded, log.2 is the old log.1, log.1 is the old log, and log.txt has the new content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_RotationIsAppliedFromFreeEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("fresh");
        await tx.AddAsync("log.txt", content);

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("older", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.2")));
        Assert.Equal("current", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.1")));
        Assert.Equal("fresh", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt")));
    }

    /// <summary>
    /// A chain of directories also moves from the free end.
    /// </summary>
    /// <remarks>
    /// <para>Given: old/a.txt and mid/b.txt exist, and next does not.</para>
    /// <para>When: Move(mid→next), then Move(old→mid), then CommitAsync.</para>
    /// <para>Then: Succeeded, next has the old mid content, mid has the old old content, and old is gone.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DirectoryChainMovesFromFreeEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string oldDir = System.IO.Path.Combine(work.Path, "old");
        string midDir = System.IO.Path.Combine(work.Path, "mid");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(midDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldDir, "a.txt"), "from-old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(midDir, "b.txt"), "from-mid");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(oldDir));
        Assert.Equal("from-old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "mid", "a.txt")));
        Assert.Equal("from-mid", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "next", "b.txt")));
    }

    /// <summary>
    /// Even if the occupied end comes first in the list, projection starts from the free end.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, c.txt does not, and Move(a→b) comes first in the list of operations.</para>
    /// <para>When: InApplyOrder and TryStamp are called.</para>
    /// <para>Then: in apply order Move(b→c) comes first, and TryStamp succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task InApplyOrder_ReversedChainPutsFreeEndFirst()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string middle = System.IO.Path.Combine(work.Path, "b.txt");
        string free = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(source, "a");
        await File.WriteAllTextAsync(middle, "b");
        JournalOperation occupiedFirst = new JournalOperation(PendingChangeKind.Move, source, newPath: middle);
        JournalOperation freeEnd = new JournalOperation(PendingChangeKind.Move, middle, newPath: free);

        JournalOperation[] ordered = StagingApplier.InApplyOrder(new[] { occupiedFirst, freeEnd });

        Assert.Equal(middle, ordered[0].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(free, ordered[0].NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(source, ordered[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(OperationOutcomes.TryStamp(new[] { occupiedFirst, freeEnd }, Guid.NewGuid(), out _, out _));
    }

    /// <summary>
    /// A cycle without a free end fails in the projection before commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, and the operations are Move(a→b) and Move(b→a).</para>
    /// <para>When: TryStamp is called.</para>
    /// <para>Then: it fails.</para>
    /// </remarks>
    [Fact]
    public async Task TryStamp_CycleFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        string left = System.IO.Path.Combine(work.Path, "a.txt");
        string right = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(left, "a");
        await File.WriteAllTextAsync(right, "b");
        JournalOperation[] cycle =
        {
            new JournalOperation(PendingChangeKind.Move, left, newPath: right),
            new JournalOperation(PendingChangeKind.Move, right, newPath: left),
        };

        Assert.False(OperationOutcomes.TryStamp(cycle, Guid.NewGuid(), out _, out _));
    }

    /// <summary>
    /// After moving aside, an Add to the source followed by a Delete removes only the Add, and the backup remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: Move(a→a.bak), Add to a.txt, Delete a.txt, then CommitAsync.</para>
    /// <para>Then: only the Move is scheduled; after Succeeded, a.bak has the old content and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddThenDeleteAtMovedSourceRemovesOnlyAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.bak")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Deleting a file that came in through a chain deletes the file it came from.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and c.txt exist.</para>
    /// <para>When: Move(a→b), Move(c→a), Delete a.txt, then CommitAsync.</para>
    /// <para>Then: Succeeded, b.txt is the old a, and neither a.txt nor c.txt exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeletingFileFromChainDeletesItsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("a", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// After an Add to a moved source, updating the backup replaces the source with new content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: Move(a→b), Add to a.txt, Update b.txt, then CommitAsync.</para>
    /// <para>Then: the scheduled operations are Add(b) and Update(a); after Succeeded, b.txt has the Update content and a.txt has the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateBackupAfterAddToSourceLeavesAddContentAtSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("a.txt", added);
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("b.txt", updated);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, change => change.Kind == PendingChangeKind.Add && change.Path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pending, change => change.Kind == PendingChangeKind.Update && change.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("updated", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// After an Add to a moved source, moving the source moves the Add content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: Move(a→b), Add to a.txt, Move(a→c), then CommitAsync.</para>
    /// <para>Then: Succeeded, b.txt is the old a, c.txt has the Add content, and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MovingSourceAfterAddMovesAddContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("a.txt", added);
        await tx.MoveAsync("a.txt", "c.txt");

        Assert.Equal("added", await tx.ReadAllTextAsync("c.txt"));
        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Moving a file that came in through a chain folds into a Move from where it came.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and c.txt exist.</para>
    /// <para>When: Move(a→b), Move(c→a), Move(a→d), then CommitAsync.</para>
    /// <para>Then: Succeeded, b.txt is the old a, d.txt is the old c, and neither a.txt nor c.txt exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MovingFileFromChainMovesItsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await tx.MoveAsync("a.txt", "d.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("a", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("c", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "d.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// Updating the destination of a Move whose source another file will move into cannot be folded, so it is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and c.txt exist.</para>
    /// <para>When: after Move(a→b) and Move(c→a), UpdateAsync on b.txt.</para>
    /// <para>Then: InvalidOperationException, and the two Moves stay scheduled.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_ThrowsAtDestinationWhoseSourceReceivesAnotherFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt"), "c");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("c.txt", "a.txt");
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync("b.txt", updated));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, change => Assert.Equal(PendingChangeKind.Move, change.Kind));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Using an already moved path with nothing coming in as a Move source again fails, because there is no source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: Move(a→b), then Move(a→c).</para>
    /// <para>Then: ExternalConflictException, and Move(a→b) stays scheduled.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_MovingMovedSourceAgainThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "c.txt"));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.EndsWith("b.txt", pending.NewPath, StringComparison.OrdinalIgnoreCase);
    }
}
