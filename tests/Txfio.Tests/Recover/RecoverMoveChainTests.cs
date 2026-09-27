using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveChainTests
{
    /// <summary>
    /// Even if a crash happens after only the head of a chain is applied, Recover finishes the remaining Moves in the same order.
    /// </summary>
    /// <remarks>
    /// <para>Given: log.txt and log.1 exist, and log.2 does not.</para>
    /// <para>When: Move(log.1→log.2) and Move(log→log.1) are scheduled, the commit is stopped right after the first apply and disposed, and RecoverAsync runs.</para>
    /// <para>Then: at the stop only log.2 is the old log.1; Recover is RolledForward, log.1 becomes the old log, and log.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesChainFromMiddleInSameOrder()
    {
        await using TempDirectory work = TempDirectory.Create();
        string current = System.IO.Path.Combine(work.Path, "log.txt");
        string older = System.IO.Path.Combine(work.Path, "log.1");
        string oldest = System.IO.Path.Combine(work.Path, "log.2");
        await File.WriteAllTextAsync(current, "current");
        await File.WriteAllTextAsync(older, "older");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("log.1", "log.2");
            await tx.MoveAsync("log.txt", "log.1");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("older", await File.ReadAllTextAsync(oldest));
        Assert.False(File.Exists(older));
        Assert.Equal("current", await File.ReadAllTextAsync(current));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("older", await File.ReadAllTextAsync(oldest));
        Assert.Equal("current", await File.ReadAllTextAsync(older));
        Assert.False(File.Exists(current));
    }

    /// <summary>
    /// Even if a crash happens after the whole chain is applied but before the journal is deleted, Recover is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: log.txt and log.1 exist, and log.2 does not.</para>
    /// <para>When: Move(log.1→log.2) and Move(log→log.1) are scheduled, the commit is stopped right after the first apply, the second is applied by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward with no skipped operations, log.2 is the old log.1, log.1 is the old log, and the journal is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardAfterWholeChainIsApplied()
    {
        await using TempDirectory work = TempDirectory.Create();
        string current = System.IO.Path.Combine(work.Path, "log.txt");
        string older = System.IO.Path.Combine(work.Path, "log.1");
        string oldest = System.IO.Path.Combine(work.Path, "log.2");
        await File.WriteAllTextAsync(current, "current");
        await File.WriteAllTextAsync(older, "older");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("log.1", "log.2");
            await tx.MoveAsync("log.txt", "log.1");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        File.Move(current, older);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Assert.Single(report.Journals).Operations);
        Assert.Equal("older", await File.ReadAllTextAsync(oldest));
        Assert.Equal("current", await File.ReadAllTextAsync(older));
        Assert.False(File.Exists(current));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "*.journal"));
    }

    /// <summary>
    /// Even if a crash happens after a whole chain of directories is applied but before the journal is deleted, Recover is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: directories v1 and cur exist, and v0 does not.</para>
    /// <para>When: Move(v1→v0) and Move(cur→v1) are scheduled, the commit is stopped right after the first apply, the second is applied by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, v0 is the old v1, v1 is the old cur, and cur does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardAfterWholeDirectoryChainIsApplied()
    {
        await using TempDirectory work = TempDirectory.Create();
        string v1 = System.IO.Path.Combine(work.Path, "v1");
        string v0 = System.IO.Path.Combine(work.Path, "v0");
        string cur = System.IO.Path.Combine(work.Path, "cur");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(cur);
        await File.WriteAllTextAsync(System.IO.Path.Combine(v1, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(cur, "new.txt"), "new");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("v1", "v0");
            await tx.MoveAsync("cur", "v1");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Directory.Move(cur, v1);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(v0, "old.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(v1, "new.txt")));
        Assert.False(Directory.Exists(cur));
    }

    /// <summary>
    /// Even if a crash happens after the Add rewritten to the source of a file Move is applied, Recover is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and b.txt does not.</para>
    /// <para>When: after Move(a.txt→b.txt), a.txt is written, the commit is stopped right after the Move is applied, the Add's .txnew is moved to a.txt by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, b.txt is the old a.txt, a.txt has the written content, and no .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsForwardAfterAddToSourceIsApplied()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt");
            await tx.WriteAllTextAsync("a.txt", "rewritten");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        File.Move(Assert.Single(Directory.GetFiles(work.Path, "*.txnew")), source);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(dest));
        Assert.Equal("rewritten", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }
}
