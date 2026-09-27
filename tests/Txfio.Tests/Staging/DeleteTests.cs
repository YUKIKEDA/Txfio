using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DeleteTests
{
    /// <summary>
    /// Delete does not delete the target before commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: the target file exists.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: one pending Delete, and the target file remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_DoesNotDeleteTargetBeforeCommit()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        Assert.True(File.Exists(target));
        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Delete, pending[0].Kind);
        Assert.Equal(target, pending[0].Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dispose without commit keeps the target file.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after a Delete.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the target file remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_DisposeWithoutCommitKeepsTarget()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteAsync("a.txt");
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// A Delete of a missing file fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: no file exists at the target path.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the target.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_MissingFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("missing.txt"));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing.txt"), ex.Path);
    }

    /// <summary>
    /// A Delete of an empty directory does not delete the target before commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: one pending Delete, and the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_EmptyDirectoryIsNotDeletedBeforeCommit()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");

        Assert.True(Directory.Exists(dir));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(dir, pending.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An Add followed by a Delete cancel each other, and the pending changes become empty.
    /// </summary>
    /// <remarks>
    /// <para>Given: the same path is added.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: no pending changes, and neither the .txnew nor the target exists.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_CancelsPrecedingAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        Assert.Empty(tx.GetPendingChanges());
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// A Delete after an Update becomes a Delete and discards the .txnew.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: one pending Delete, there is no .txnew, and the target remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_AfterUpdateBecomesDeleteAndDeletesTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        Assert.Equal(PendingChangeKind.Delete, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// An Add after a Delete becomes an Update.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is deleted.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: the pending kind is Update, and the target still remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_AfterDeleteBecomesUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Update, pending[0].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Even if the journal write fails for a Delete after an Add, the pending change and .txnew remain.
    /// </summary>
    /// <remarks>
    /// <para>Given: after the same path is added, the journal is locked exclusively.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: an exception, the pending change stays Add, and the .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_JournalWriteFailureAfterAddKeepsAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        await Assert.ThrowsAsync<IOException>(() => tx.DeleteAsync("a.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Even if the journal write fails for a Delete after an Update, the pending change and .txnew remain.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an existing file is updated, the journal is locked exclusively.</para>
    /// <para>When: DeleteAsync is called.</para>
    /// <para>Then: an exception, the pending change stays Update, and the .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_JournalWriteFailureAfterUpdateKeepsUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        await Assert.ThrowsAsync<IOException>(() => tx.DeleteAsync("a.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Update, pending[0].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
