using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverContentionTests
{
    /// <summary>
    /// A journal whose footprint a live transaction holds is reported as Contended, and the other journals are still processed.
    /// </summary>
    /// <remarks>
    /// <para>Given: committing Adds of held.txt and b.txt are left, and a lock set holds the path lock of held.txt (as a live transaction that took it would).</para>
    /// <para>When: RecoverAsync runs, the lock set is released, and RecoverAsync runs again.</para>
    /// <para>Then: the first is Contended, lists held.txt's journal as Contended and the other as RolledForward, and b.txt exists. The second is RolledForward and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ReportsContendedAndProcessesOtherJournals()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles other = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "b.txt", "other");
        (PathLockSet holder, Guid crashedId) = await HoldOrphanPathAsync(work.Path);
        try
        {
            RecoverReport first = await global::Txfio.Txfio.RecoverAsync(work.Path);

            Assert.Equal(RecoverResult.Contended, first.Result);
            Assert.Equal(2, first.Journals.Count);
            Assert.Contains(first.Journals, journal => journal.TransactionId == crashedId && journal.Result == RecoverResult.Contended);
            Assert.Contains(first.Journals, journal => journal.TransactionId != crashedId && journal.Result == RecoverResult.RolledForward);
            Assert.Equal("other", await File.ReadAllTextAsync(other.TargetPath));
        }
        finally
        {
            holder.Release();
        }

        RecoverReport second = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, second.Result);
        Assert.Equal(crashedId, Assert.Single(second.Journals).TransactionId);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// A contended journal is retried until the deadline and processed once its paths are released.
    /// </summary>
    /// <remarks>
    /// <para>Given: a committing Add of held.txt is left, and a lock set holds the path lock of held.txt.</para>
    /// <para>When: RecoverAsync starts with a 10-second wait, and the lock set is released about 300 ms later.</para>
    /// <para>Then: RecoverAsync returns RolledForward and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RetriesContendedJournalUntilPathsAreReleased()
    {
        await using TempDirectory work = TempDirectory.Create();
        (PathLockSet holder, _) = await HoldOrphanPathAsync(work.Path);
        Task<RecoverReport> recovering;
        try
        {
            recovering = global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            Assert.False(recovering.IsCompleted);
        }
        finally
        {
            holder.Release();
        }

        RecoverReport report = await recovering;

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Cancelling between rounds returns the report instead of throwing, so earlier results are kept.
    /// </summary>
    /// <remarks>
    /// <para>Given: committing Adds of held.txt and b.txt are left, and a lock set holds the path lock of held.txt.</para>
    /// <para>When: RecoverAsync starts with no deadline, and its token is cancelled about 300 ms later.</para>
    /// <para>Then: RecoverAsync returns Contended without OperationCanceledException, with b.txt's journal RolledForward, and held.txt's journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ReturnsReportWhenCancelledBetweenRounds()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles other = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "b.txt", "other");
        (PathLockSet holder, _) = await HoldOrphanPathAsync(work.Path);
        using CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path, Timeout.InfiniteTimeSpan, cancel.Token);

        Assert.Equal(RecoverResult.Contended, report.Result);
        Assert.Contains(report.Journals, journal => journal.Result == RecoverResult.RolledForward);
        Assert.Equal("other", await File.ReadAllTextAsync(other.TargetPath));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        holder.Release();
    }

    /// <summary>
    /// A journal whose footprint has one entry under another does not contend with itself.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction creates directory d, adds d/a.txt, and is stopped at AfterCommitting and disposed, so its footprint has d and d/a.txt.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward (not Contended), d/a.txt has the Add content, and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardJournalWithNestedFootprintEntries()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await crashed.CreateDirectoryAsync("d");
            await crashed.WriteAllTextAsync("d/a.txt", "staged");
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Equal(RecoverResult.RolledForward, Assert.Single(report.Journals).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "d", "a.txt")));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    // Leaves a committing Add of held.txt, and holds its path lock the way a live transaction that took it would (with the shared work-folder lock).
    private static async Task<(PathLockSet Holder, Guid OrphanId)> HoldOrphanPathAsync(string workFolder)
    {
        LeftoverAddFiles held = await LeftoverAddFiles.WriteAddAsync(workFolder, committing: true, "held.txt", "held");
        Assert.True(MetadataNames.TryGetTransactionId(held.JournalPath, out Guid orphanId));
        PathLockSet holder = new PathLockSet();
        await holder.AcquireSharedAsync(workFolder, default);
        await holder.AcquireAsync(workFolder, new[] { held.TargetPath }, default);
        return (holder, orphanId);
    }
}
