using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class MoveChainTests
{
    /// <summary>
    /// When called from the free end, Moves of different files stay as two.
    /// </summary>
    /// <remarks>
    /// <para>Given: log.txt and log.1 exist, and log.2 does not.</para>
    /// <para>When: Move(log.1→log.2), then Move(log→log.1).</para>
    /// <para>Then: two pending Moves, and the files on disk have not moved yet.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ChainOfDifferentFilesIsNotFolded()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.EndsWith("log.1", pending[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("log.2", pending[0].NewPath, StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.Move, pending[1].Kind);
        Assert.EndsWith("log.txt", pending[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("log.1", pending[1].NewPath, StringComparison.Ordinal);
        Assert.Equal("older", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.1")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "log.2")));
    }

    /// <summary>
    /// An Add to the source of a file Move works even though the file is on disk.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is moved to a.bak.</para>
    /// <para>When: AddAsync is called on a.txt.</para>
    /// <para>Then: the pending changes are the Move and the Add, and the old content of a.txt remains on disk.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_CanWriteToFileMoveSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.AddAsync("a.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Add, pending[1].Kind);
        Assert.Equal(source, pending[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// An Add to the source of a directory Move fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: dir is moved to dir.bak.</para>
    /// <para>When: AddAsync is called on dir.</para>
    /// <para>Then: InvalidOperationException, and the pending change stays the Move.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DirectoryMoveSourceFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "dir"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("dir", "dir.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("dir", content));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
    }

    /// <summary>
    /// A Move fails when the destination exists and is not the source of another Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist.</para>
    /// <para>When: Move(a→b).</para>
    /// <para>Then: ExternalConflictException with Path set to the destination, and no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_FailsWhenDestinationIsNotAnotherMoveSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "dst");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "b.txt"));

        Assert.Equal(dest, ex.Path);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Moves that are each other's destinations are rejected as a cycle.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and b.txt does not.</para>
    /// <para>When: Move(a→b), then Move(b→a).</para>
    /// <para>Then: the second throws InvalidOperationException, the pending changes stay one, and no file has moved.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_RejectsCycle()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("b.txt", "a.txt"));

        Assert.Contains("A move without a free end is not accepted", ex.Message, StringComparison.Ordinal);
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.EndsWith("b.txt", pending.NewPath, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A swap through a temporary name fails at the third Move, because folding makes a cycle.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, and tmp does not.</para>
    /// <para>When: Move(a→tmp), Move(b→a), and Move(tmp→b) are called in order.</para>
    /// <para>Then: the third throws InvalidOperationException, and the pending changes stay the first two Moves.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_SwapThroughTemporaryNameFailsAtThirdMove()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "b");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "tmp");
        await tx.MoveAsync("b.txt", "a.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("tmp", "b.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.EndsWith("tmp", pending[0].NewPath, StringComparison.Ordinal);
        Assert.EndsWith("a.txt", pending[1].NewPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Directories also keep a chain when called from the free end.
    /// </summary>
    /// <remarks>
    /// <para>Given: old and mid exist, and next does not.</para>
    /// <para>When: Move(mid→next), then Move(old→mid).</para>
    /// <para>Then: two pending directory Moves.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DirectoryChainIsNotFolded()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "old"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "mid"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Move, pending[1].Kind);
        Assert.EndsWith("next", pending[0].NewPath, StringComparison.Ordinal);
        Assert.EndsWith("mid", pending[1].NewPath, StringComparison.Ordinal);
    }
}
