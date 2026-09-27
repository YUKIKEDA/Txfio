using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLivenessTests
{
    /// <summary>
    /// While a transaction is making changes, Recover cannot take the work-folder lock and does nothing.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has an Add and a CreateDirectory, and has written into the created directory with the plain file API.</para>
    /// <para>When: RecoverAsync runs without Dispose or commit, then CommitAsync runs on the same transaction.</para>
    /// <para>Then: LockContentionException (Path is the work folder); the journal, the .txnew, and the directory contents remain; and the commit is Succeeded.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DoesNothingWithLockContentionWhileTransactionIsActive()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string drop = System.IO.Path.Combine(work.Path, "drop");
        string raw = System.IO.Path.Combine(drop, "raw.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(raw, "raw");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => global::Txfio.Txfio.RecoverAsync(work.Path));

        Assert.Equal(work.Path, contention.Path);
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("raw", await File.ReadAllTextAsync(raw));

        CommitReport committed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, committed.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("raw", await File.ReadAllTextAsync(raw));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    /// <summary>
    /// The liveness lock of a live transaction has the same name as its journal, and is gone when the transaction ends.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: the tx-*.lock files in .txfio are counted before commit and after an empty CommitAsync.</para>
    /// <para>Then: before commit there is one with the same name as the journal, and after commit there is none.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_LivenessLockPairsWithJournalAndGoesAwayAtCommit()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string journal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        string liveness = Assert.Single(Directory.GetFiles(metadata, "tx-*.lock"));
        Assert.Equal(System.IO.Path.ChangeExtension(journal, ".lock"), liveness);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);

        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
    }

    /// <summary>
    /// The liveness lock of a discarded transaction is gone, and there is nothing to recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction with an Add.</para>
    /// <para>When: it is disposed without commit, and RecoverAsync runs.</para>
    /// <para>Then: there is no tx-*.lock, and the result is NoPendingTransactions.</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DiscardRemovesLivenessLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
        }

        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
        Assert.Equal(RecoverResult.NoPendingTransactions, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
    }

    /// <summary>
    /// Even after Committing is written, Recover does not run while the owner is alive.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is stopped at AfterCommitting and not disposed yet.</para>
    /// <para>When: RecoverAsync runs, the transaction is disposed, and RecoverAsync runs again.</para>
    /// <para>Then: the first is LockContentionException and the target does not exist; the second is RolledForward and the target has the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DoesNotTouchCommittingWhileOwnerIsAlive()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults);
        await using (MemoryStream content = LeftoverAddFiles.Utf8Stream("staged"))
        {
            await tx.AddAsync("a.txt", content);
        }

        await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());

        await Assert.ThrowsAsync<LockContentionException>(() => global::Txfio.Txfio.RecoverAsync(work.Path));
        Assert.False(File.Exists(target));

        await tx.DisposeAsync();

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Even when a live transaction is skipped, a crashed transaction is recovered.
    /// </summary>
    /// <remarks>
    /// <para>Given: a live transaction with no operations yet, and another transaction stopped at AfterCommitting and disposed.</para>
    /// <para>When: RecoverAsync runs, and the live one adds and commits.</para>
    /// <para>Then: RolledForward applies the stopped Add, the live one's journal and liveness lock remain, and its commit is Succeeded.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RecoversOnlyCrashedTransaction()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("crashed");
            await crashed.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("crashed", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.lock"));
        await using MemoryStream liveContent = LeftoverAddFiles.Utf8Stream("live");
        await live.AddAsync("b.txt", liveContent);
        Assert.Equal(CommitResult.Succeeded, (await live.CommitAsync()).Result);
        Assert.Equal("live", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A journal without a liveness lock is rolled back as a crashed transaction.
    /// </summary>
    /// <remarks>
    /// <para>Given: only an uncommitted journal exists, and there is no tx-*.lock (a journal left by a version before liveness locks).</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and neither the journal nor any tx-*.lock remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsBackJournalWithoutLivenessLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
    }
}
