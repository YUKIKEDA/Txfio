using Txfio.Tests.Support;

namespace Txfio.Tests;

public sealed class TxfioTests
{
    /// <summary>
    /// The library assembly is named Txfio.
    /// </summary>
    /// <remarks>
    /// <para>Given: the tests reference Txfio.</para>
    /// <para>When: the assembly name of the public type Txfio is read.</para>
    /// <para>Then: the name is Txfio.</para>
    /// </remarks>
    [Fact]
    public void Assembly_IsNamedTxfio()
    {
        Assert.Equal("Txfio", typeof(global::Txfio.Txfio).Assembly.GetName().Name);
    }

    /// <summary>
    /// A transaction cannot begin on a work folder that does not exist.
    /// </summary>
    /// <remarks>
    /// <para>Given: there is no directory at the given path.</para>
    /// <para>When: BeginAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is that folder.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_MissingFolderThrowsExternalConflictException()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-" + Guid.NewGuid().ToString("N"));
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => global::Txfio.Txfio.BeginAsync(missing));
        Assert.Equal(System.IO.Path.GetFullPath(missing), ex.Path);
    }

    /// <summary>
    /// A work folder that does not exist cannot be recovered.
    /// </summary>
    /// <remarks>
    /// <para>Given: there is no directory at the given path.</para>
    /// <para>When: RecoverAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is that folder.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_MissingFolderThrowsExternalConflictException()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-" + Guid.NewGuid().ToString("N"));
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => global::Txfio.Txfio.RecoverAsync(missing));
        Assert.Equal(System.IO.Path.GetFullPath(missing), ex.Path);
    }

    /// <summary>
    /// Beginning creates a journal, and Dispose without commit deletes it.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: BeginAsync, then Dispose without Commit.</para>
    /// <para>Then: right after begin there is a tx-*.journal, and after Dispose there is none.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_DisposeWithoutCommitDeletesJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string journal;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            string[] files = Directory.GetFiles(
                System.IO.Path.Combine(work.Path, ".txfio"),
                "tx-*.journal");
            Assert.Single(files);
            journal = files[0];
            Assert.True(File.Exists(journal));
            Assert.Empty(tx.GetPendingChanges());
        }

        Assert.False(File.Exists(journal));
    }

    /// <summary>
    /// An empty commit succeeds and deletes the journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun on an empty work folder.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_EmptyTransactionSucceedsAndDeletesJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);

        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    /// <summary>
    /// Without unfinished journals, Recover does nothing.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: NoPendingTransactions.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_NoJournalReturnsNoPendingTransactions()
    {
        await using TempDirectory work = TempDirectory.Create();
        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.NoPendingTransactions, result.Result);
    }

    /// <summary>
    /// Recover deletes an orphaned journal that is not Committing.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and only an uncommitted journal remains.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and the file is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesUncommittedJournalAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        string journal = await WriteLeftoverJournalAsync(work.Path, committing: false);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(journal));
    }

    /// <summary>
    /// Recover rolls forward an orphaned Committing journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and only a Committing journal remains.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and the file is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardCommittingJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string journal = await WriteLeftoverJournalAsync(work.Path, committing: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(journal));
    }

    private static async Task<string> WriteLeftoverJournalAsync(string workFolder, bool committing)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") + "\",\"committing\":" + committingLiteral + "}";
        await File.WriteAllTextAsync(journalPath, json);
        return journalPath;
    }
}
