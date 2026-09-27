using Txfio.Tests.Support;

namespace Txfio.Tests.Journal;

public sealed class JournalAppendTests
{
    /// <summary>
    /// Operations that only add to the end append one line each to the journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: only the work folder exists.</para>
    /// <para>When: a.txt, b.txt, and c.txt are added in order, and the journal lines are counted.</para>
    /// <para>Then: there are four lines, the document on the first line and three appended lines, and the operation paths are a.txt, b.txt, and c.txt.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_AppendsToJournalWhenOnlyAddingToEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");
        await tx.WriteAllTextAsync("c.txt", "c");

        string journal = await File.ReadAllTextAsync(JournalOf(work.Path));
        Assert.Equal(4, journal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"path\":\"a.txt\"", journal, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"b.txt\"", journal, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"c.txt\"", journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// An operation that changes the middle rewrites the journal as a one-line document.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt are added.</para>
    /// <para>When: a.txt is deleted, canceling its Add.</para>
    /// <para>Then: the journal is one line, and the only operation is the Add of b.txt.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_RewritesJournalWhenMiddleChanges()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");

        await tx.DeleteAsync("a.txt");

        Assert.Single(await File.ReadAllLinesAsync(JournalOf(work.Path)));
        Assert.Equal(System.IO.Path.Combine(work.Path, "b.txt"), Assert.Single(tx.GetPendingChanges()).Path);
    }

    /// <summary>
    /// Recover rolls back an appended journal, including the appended operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: after three Adds, the transaction is disposed without rollback (the same as a crash).</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, no .txnew remains, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsBackAppendedJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            await tx.WriteAllTextAsync("b.txt", "b");
            await tx.WriteAllTextAsync("c.txt", "c");
            faults.SuppressRollback();
        }

        Assert.Equal(3, Directory.GetFiles(work.Path, "*.txnew").Length);

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// A last line that does not end with a newline is dropped as a crash during an append.
    /// </summary>
    /// <remarks>
    /// <para>Given: the journal with the Add of a.txt ends with a half-written line without a newline.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and the .txnew of a.txt is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DropsHalfWrittenLastLine()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            faults.SuppressRollback();
        }

        await File.AppendAllTextAsync(JournalOf(work.Path), "{\"append\":[{\"kind\":\"Add\",\"pa");

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// A line that ends with a newline but cannot be read makes the journal unreadable.
    /// </summary>
    /// <remarks>
    /// <para>Given: the journal with the Add of a.txt ends with a broken line that ends with a newline.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, and the journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_BrokenLineEndingWithNewlineMakesJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            faults.SuppressRollback();
        }

        string journal = JournalOf(work.Path);
        await File.AppendAllTextAsync(journal, "{broken\n");

        Assert.Equal(RecoverResult.JournalUnreadable, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.True(File.Exists(journal));
    }

    private static string JournalOf(string workFolder)
    {
        return Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal").Single();
    }
}
