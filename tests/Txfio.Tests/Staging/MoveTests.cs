using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class MoveTests
{
    /// <summary>
    /// Move does not move the target before commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: the target file exists.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: one pending Move, the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DoesNotMoveTargetBeforeCommit()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dispose without commit keeps the source file.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after a Move.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DisposeWithoutCommitKeepsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("a.txt", "b.txt");
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A Move of a missing file fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: no file exists at the source.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the source.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_MissingFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("missing.txt", "b.txt"));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing.txt"), ex.Path);
    }

    /// <summary>
    /// A Move fails when the destination already exists.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists at the destination.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the destination.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ExistingDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "dst");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "b.txt"));
        Assert.Equal(dest, ex.Path);
    }

    /// <summary>
    /// A Move fails when the destination is a directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: the source is a file, and the destination is a directory.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the destination.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DirectoryDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dest);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "sub"));
        Assert.Equal(dest, ex.Path);
    }

    /// <summary>
    /// A Move fails when the destination's parent does not exist.
    /// </summary>
    /// <remarks>
    /// <para>Given: the destination's parent directory does not exist.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the destination's parent.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_MissingParentThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "missing/b.txt"));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), ex.Path);
    }

    /// <summary>
    /// A Move after an Add moves the target of the Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: the same path is added.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: one pending Add (at the destination), and the .txnew has the destination's name in the destination's directory.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_AfterAddBecomesAddAtDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "sub/b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// After an Add is moved, updating the destination rewrites the destination's .txnew.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, it is moved to another directory.</para>
    /// <para>When: UpdateAsync is called on the destination.</para>
    /// <para>Then: one pending Add (at the destination), and exactly one .txnew in the destination's directory with the new content.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_RewritesTxnewOfMovedAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "sub/b.txt");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.UpdateAsync("sub/b.txt", second);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        string[] sidecars = Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew");
        Assert.Single(sidecars);
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Even if the journal write fails for an Update at a moved Add, the destination's .txnew keeps its earlier content.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is moved, and the journal is locked exclusively.</para>
    /// <para>When: UpdateAsync is called on the destination.</para>
    /// <para>Then: IOException, the pending change stays Add, and there is one .txnew in the destination's directory with the earlier content.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_JournalWriteFailureAtMovedAddKeepsTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "sub/b.txt");
        await using FileStream journalLock = LockJournal(work.Path);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("sub/b.txt", second));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
        Assert.Equal(
            "first",
            await File.ReadAllTextAsync(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew")[0]));
    }

    /// <summary>
    /// A Move after an Update becomes an Add at the destination and a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: MoveAsync is called.</para>
    /// <para>Then: the pending changes are an Add and a Delete, the .txnew moves to the destination's directory, and the source file remains.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_AfterUpdateBecomesAddAndDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "sub/b.txt");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// A Move after a Move folds from the start to the end.
    /// </summary>
    /// <remarks>
    /// <para>Given: A is moved to B.</para>
    /// <para>When: MoveAsync moves B to C.</para>
    /// <para>Then: one pending Move(A→C).</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ConsecutiveMovesFoldFromStartToEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("b.txt", "c.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// An Add at a Move destination fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: A is moved to B.</para>
    /// <para>When: AddAsync is called on B.</para>
    /// <para>Then: InvalidOperationException, and the pending change stays the Move.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_MoveDestinationThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("b.txt", content));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// An Update at a Move destination becomes an Add at the destination and a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: A is moved to B.</para>
    /// <para>When: UpdateAsync is called on B.</para>
    /// <para>Then: the pending changes are Add(B) and Delete(A), the .txnew is on B's side, and the source file remains.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_MoveDestinationBecomesAddAndDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(dest, pending[0].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal(source, pending[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(dest));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Dispose without commit after an Update at a Move destination keeps the source and deletes the .txnew.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Move, the destination is updated.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the source remains, and neither the destination nor the .txnew exists.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_DisposeWithoutCommitAtMoveDestinationKeepsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("a.txt", "b.txt");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("b.txt", content);
        }

        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// A Delete at a Move destination becomes a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: A is moved to B.</para>
    /// <para>When: DeleteAsync is called on B.</para>
    /// <para>Then: one pending Delete(A), the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_MoveDestinationBecomesSourceDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Deleting a Move destination after updating it cancels the Add, leaving only the Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: after Move(A→B), B is updated.</para>
    /// <para>When: DeleteAsync is called on B.</para>
    /// <para>Then: one pending Delete(A), no .txnew, the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_AfterUpdatingMoveDestinationLeavesOnlySourceDelete()
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

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// A Delete at a Move source replaces the Move with a Delete.
    /// </summary>
    /// <remarks>
    /// <para>Given: A is moved to B.</para>
    /// <para>When: DeleteAsync is called on A.</para>
    /// <para>Then: one pending Delete(A), the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_MoveSourceBecomesDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("a.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Even if the journal write fails for a Move after an Add, the pending change and .txnew stay as they were.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, the journal is locked exclusively.</para>
    /// <para>When: MoveAsync moves it to another directory.</para>
    /// <para>Then: IOException, the pending change stays Add, and the .txnew is still in its original place.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_JournalWriteFailureAfterAddKeepsAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.MoveAsync("a.txt", "sub/b.txt"));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "a.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Even if the journal write fails for an Update at a Move destination, the pending change stays the Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Move, the journal is locked exclusively.</para>
    /// <para>When: UpdateAsync is called on the destination.</para>
    /// <para>Then: IOException, the pending change stays the Move, and there is no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_JournalWriteFailureAtMoveDestinationKeepsMove()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using FileStream journalLock = LockJournal(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("b.txt", content));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "a.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// A Move that differs only in case fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: it is moved to A.txt.</para>
    /// <para>Then: InvalidOperationException, no pending changes, no locks, and a.txt remains with the same content.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_DifferingOnlyInCaseThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "A.txt"));

        Assert.Contains("A path cannot be moved to itself", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.Equal("a.txt", System.IO.Path.GetFileName(Assert.Single(Directory.GetFiles(work.Path, "a.txt"))));
    }

    /// <summary>
    /// A Move to exactly the same path fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: it is moved from a.txt to a.txt.</para>
    /// <para>Then: InvalidOperationException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_SamePathThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "a.txt"));

        Assert.Contains("A path cannot be moved to itself", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Calling a Move to another path again with only a different spelling keeps the original scheduled Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is moved to b.txt.</para>
    /// <para>When: a.txt is moved to B.txt.</para>
    /// <para>Then: no exception, and the pending change stays one Move from a.txt to b.txt.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_SameDestinationWithDifferentSpellingKeepsSchedule()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await tx.MoveAsync("a.txt", "B.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal("a.txt", System.IO.Path.GetFileName(pending.Path), StringComparer.Ordinal);
        Assert.Equal("b.txt", System.IO.Path.GetFileName(pending.NewPath), StringComparer.Ordinal);
    }

    /// <summary>
    /// A replacing Move replaces the existing destination file with the source at commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist.</para>
    /// <para>When: Move(a.txt→b.txt, overwrite: true) is scheduled, and the disk is checked before and after commit.</para>
    /// <para>Then: before commit both are unchanged; after commit b.txt has the old content of a.txt, and neither a.txt nor any .txnew exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OverwriteReplacesDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(dest, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(dest));
        Assert.Equal("new", await tx.ReadAllTextAsync("b.txt"));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// A Delete at the destination folds into the replacing Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist.</para>
    /// <para>When: Delete(b.txt), then Move(a.txt→b.txt, overwrite: true), then commit.</para>
    /// <para>Then: the pending operations are one Move, and after commit b.txt has the old content of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_FoldsDestinationDeleteIntoReplacingMove()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.DeleteAsync("b.txt");
        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        PendingChange change = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, change.Kind);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// When the destination does not exist, it is a normal Move even with overwrite.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and b.txt does not.</para>
    /// <para>When: Move(a.txt→b.txt, overwrite: true), the journal is read, and the transaction commits.</para>
    /// <para>Then: the journal has no overwrite, and after commit b.txt exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_BecomesNormalMoveWhenDestinationIsMissing()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        Assert.DoesNotContain("overwrite", await File.ReadAllTextAsync(journal), StringComparison.Ordinal);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A file swaps out a directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt, and a directory d with a child old.txt.</para>
    /// <para>When: Move(a.txt→d, overwrite: true), the post-commit view is checked, and the transaction commits.</para>
    /// <para>Then: in the view d has the content of a.txt and d/old.txt does not exist; after commit d is a file, and neither a.txt nor .txold exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OverwriteSwapsDirectoryForFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "d");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "file");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(System.IO.Path.Combine(target, "old.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("a.txt", "d", overwrite: true);

        Assert.Equal("file", await tx.ReadAllTextAsync("d"));
        Assert.False(await tx.ExistsAsync("d/old.txt"));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(work.Path, "*.txold"));
    }

    /// <summary>
    /// A directory swaps out a file.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file x, and a directory d with a child new.txt.</para>
    /// <para>When: Move(d→x, overwrite: true), then commit.</para>
    /// <para>Then: x is a directory with new.txt, and neither d nor .txold exists.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_OverwriteSwapsFileForDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "x");
        await File.WriteAllTextAsync(target, "file");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "d", "new.txt"), "new");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("d", "x", overwrite: true);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "new.txt")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "d")));
        Assert.Empty(Directory.GetFileSystemEntries(work.Path, "*.txold"));
    }

    /// <summary>
    /// A CreateDirectory at the same path after a Delete, and an Add after a Delete of an empty directory, are not accepted.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file x and an empty directory e.</para>
    /// <para>When: CreateDirectory(x) after Delete(x), and an Add to e after Delete(e).</para>
    /// <para>Then: both throw InvalidOperationException, and the operations stay two Deletes.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_KindAtPathCannotChangeAfterDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "x"), "file");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "e"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("x");
        await tx.DeleteAsync("e");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateDirectoryAsync("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("e", "file"));

        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// No further operation is allowed on the source or destination of a replacing Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, and Move(a.txt→b.txt, overwrite: true) is scheduled.</para>
    /// <para>When: a write to b.txt, a write to a.txt, a Delete of b.txt, and a Move of b.txt.</para>
    /// <para>Then: each throws InvalidOperationException, and the operations stay one Move.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_NoFurtherOperationOnReplacingMoveSourceOrDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("b.txt", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("a.txt", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync("b.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("b.txt", "c.txt"));

        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// Moving a staged Add with a replacing Move becomes an Update of the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: b.txt exists.</para>
    /// <para>When: a.txt is added, Move(a.txt→b.txt, overwrite: true), then commit.</para>
    /// <para>Then: the pending operation is one Update of b.txt, and after commit b.txt has the added content.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ReplacingWithStagedAddBecomesDestinationUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "staged");

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        PendingChange change = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Update, change.Kind);
        Assert.Equal(dest, change.Path);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(dest));
    }

    /// <summary>
    /// An Update at a Move destination is recorded in the journal before the destination's .txnew is written.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and Move(a.txt→b.txt) is scheduled.</para>
    /// <para>When: UpdateAsync is called on b.txt, and the journal is read on progress while writing.</para>
    /// <para>Then: at every progress report the journal contains the .txnew of b.txt, and at the end it is folded into an Add and a Delete.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_RecordsJournalBeforeTxnewAtMoveDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        JournalProbeProgress probe = new JournalProbeProgress(work.Path, "b.txt.");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.UpdateAsync("b.txt", content, probe);

        Assert.True(probe.Reported);
        Assert.True(probe.AlwaysJournaled);
        Assert.Equal(
            new[] { PendingChangeKind.Add, PendingChangeKind.Delete },
            tx.GetPendingChanges().Select(static change => change.Kind).OrderBy(static kind => kind).ToArray());
    }

    /// <summary>
    /// Moving a staged Add renames the .txnew to the destination's name, and the journal points to it.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added.</para>
    /// <para>When: Move(a.txt→c.txt).</para>
    /// <para>Then: there is only one .txnew, with the name of c.txt, and the journal points to the .txnew of c.txt, not of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_MovesStagedAddTxnewAndJournalToDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        await tx.MoveAsync("a.txt", "c.txt");

        string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.StartsWith("c.txt.", System.IO.Path.GetFileName(staging), StringComparison.Ordinal);
        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string text = await File.ReadAllTextAsync(journal);
        Assert.Contains(System.IO.Path.GetFileName(staging), text, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt.", text, StringComparison.Ordinal);
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
