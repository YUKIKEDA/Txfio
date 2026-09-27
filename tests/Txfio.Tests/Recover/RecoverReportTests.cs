using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverReportTests
{
    /// <summary>
    /// ConflictDetected lists the skipped operations, and the next Recover does not include that journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of a Committing Add, and the target path has already been created externally.</para>
    /// <para>When: RecoverAsync is called twice.</para>
    /// <para>Then: the first is ConflictDetected with the journal's operation as BeforeAfterMismatch, and the second is NoPendingTransactions with an empty journal list.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ListsConflictedOperations()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.TargetPath, "external");
        Assert.True(MetadataNames.TryGetTransactionId(leftover.JournalPath, out Guid transactionId));

        RecoverReport first = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, first.Result);
        JournalReport journal = Assert.Single(first.Journals);
        Assert.Equal(transactionId, journal.TransactionId);
        Assert.Equal(RecoverResult.ConflictDetected, journal.Result);
        OperationReport operation = Assert.Single(journal.Operations);
        Assert.Equal(OperationDisposition.Skipped, operation.Disposition);
        Assert.Equal(OperationFailureReason.BeforeAfterMismatch, operation.Reason);
        Assert.Equal(PendingChangeKind.Add, operation.Kind);
        Assert.EndsWith("a.txt", operation.Path, StringComparison.OrdinalIgnoreCase);

        RecoverReport second = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.NoPendingTransactions, second.Result);
        Assert.Empty(second.Journals);
    }

    /// <summary>
    /// With several journals, it returns both the overall priority and the result of each journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: a broken journal and leftovers of an uncommitted Add.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: the overall result is JournalUnreadable, the broken one has an empty operation list, and the uncommitted one is RolledBack.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ReturnsPriorityAndBreakdownForSeveralJournals()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles broken = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "broken.txt",
            "staged");
        await File.WriteAllTextAsync(broken.JournalPath, "{\"version\":1,\"transac");
        LeftoverAddFiles pending = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "pending.txt",
            "staged");
        Assert.True(MetadataNames.TryGetTransactionId(broken.JournalPath, out Guid brokenId));
        Assert.True(MetadataNames.TryGetTransactionId(pending.JournalPath, out Guid pendingId));

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.Equal(2, report.Journals.Count);
        JournalReport unreadable = Assert.Single(report.Journals, journal => journal.TransactionId == brokenId);
        Assert.Equal(RecoverResult.JournalUnreadable, unreadable.Result);
        Assert.Empty(unreadable.Operations);
        JournalReport rolledBack = Assert.Single(report.Journals, journal => journal.TransactionId == pendingId);
        Assert.Equal(RecoverResult.RolledBack, rolledBack.Result);
        Assert.Empty(rolledBack.Operations);
    }

    /// <summary>
    /// Live journals are not listed.
    /// </summary>
    /// <remarks>
    /// <para>Given: a live transaction with no operations, and a transaction stopped at AfterCommitting and disposed.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: the list has only the crashed one, and not the live transaction's ID.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DoesNotListLiveJournals()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        string liveJournal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(MetadataNames.TryGetTransactionId(liveJournal, out Guid liveId));
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await crashed.WriteAllTextAsync("a.txt", "crashed");
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        JournalReport journal = Assert.Single(report.Journals);
        Assert.NotEqual(liveId, journal.TransactionId);
        Assert.Equal(RecoverResult.RolledForward, journal.Result);
        Assert.Empty(journal.Operations);
        Assert.Empty(live.GetPendingChanges());
    }

    /// <summary>
    /// Several orphaned journals are listed in lexical order of the path, ignoring case.
    /// </summary>
    /// <remarks>
    /// <para>Given: a live transaction exists, and two sets of leftovers of uncommitted Adds are written in reverse lexical order.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: Journals are in lexical order of the path ignoring case, both are RolledBack, and the live transaction is not in the list.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ListsSeveralJournalsInLexicalOrderIgnoringCase()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string liveJournal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(MetadataNames.TryGetTransactionId(liveJournal, out Guid liveId));
        Guid later = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        Guid earlier = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "z.txt", "z", later);
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "a.txt", "a", earlier);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, report.Result);
        Assert.Equal(2, report.Journals.Count);
        Assert.Equal(earlier, report.Journals[0].TransactionId);
        Assert.Equal(RecoverResult.RolledBack, report.Journals[0].Result);
        Assert.Equal(later, report.Journals[1].TransactionId);
        Assert.Equal(RecoverResult.RolledBack, report.Journals[1].Result);
        Assert.DoesNotContain(report.Journals, journal => journal.TransactionId == liveId);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "z.txt")));
        Assert.Empty(live.GetPendingChanges());
    }

    /// <summary>
    /// An unreadable journal whose ID cannot be taken from its file name is not listed with an empty ID.
    /// </summary>
    /// <remarks>
    /// <para>Given: a broken journal whose file name matches tx-*.journal but is not a GUID.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: the overall result is JournalUnreadable, the list is empty, and the journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DoesNotListUnreadableJournalWithoutId()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        string journal = System.IO.Path.Combine(metadata, "tx-not-a-guid.journal");
        await File.WriteAllTextAsync(journal, "{\"version\":1,\"transac");

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.Empty(report.Journals);
        Assert.True(File.Exists(journal));
    }
}
