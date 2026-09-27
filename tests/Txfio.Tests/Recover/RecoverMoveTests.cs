using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveTests
{
    /// <summary>
    /// Recover of an uncommitted Move keeps the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a Move journal and its source file remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the journal is deleted, the source remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UncommittedMoveKeepsSourceAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: false,
            "a.txt",
            "b.txt",
            "keep");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(leftover.SourcePath));
        Assert.False(File.Exists(leftover.DestPath));
    }

    /// <summary>
    /// Recover finishes the leftovers of a Committing Move.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a Committing Move journal and its source remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, the destination has the content, and neither the source nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesCommittingMoveAndRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: true,
            "a.txt",
            "b.txt",
            "gone");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.Equal("gone", await File.ReadAllTextAsync(leftover.DestPath));
    }

    /// <summary>
    /// If a Committing Move has already been applied, Recover treats it as done and deletes the journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing Move journal, with no source and only the destination.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, there is no journal, and the destination remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AlreadyMovedCommittingRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: true,
            "a.txt",
            "b.txt",
            "done",
            alreadyMoved: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.Equal("done", await File.ReadAllTextAsync(leftover.DestPath));
    }

    /// <summary>
    /// Even if a replacing Move is stopped right after Committing, Recover finishes the replacement.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, and Move(a.txt→b.txt, overwrite: true) is scheduled.</para>
    /// <para>When: the commit is stopped right after Committing and disposed, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, b.txt has the old content of a.txt, and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesReplacingMove()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Even if a crash happens after a replacing Move is applied, Recover is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist, and Move(a.txt→b.txt, overwrite: true) is scheduled.</para>
    /// <para>When: the commit is stopped right after apply and disposed, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, no operation is skipped, and b.txt has the old content of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AppliedReplacingMoveRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Assert.Single(report.Journals).Operations);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// Even if a directory swap is stopped right after Committing, Recover finishes the swap.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt and build/new.txt exist, and Move(build→site, overwrite: true) is scheduled.</para>
    /// <para>When: the commit is stopped right after Committing and disposed, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, site has only new.txt, and there is no .txold.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesDirectorySwap()
    {
        await using TempDirectory work = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "build", "new.txt"), "new");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("build", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal(new[] { "new.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.Empty(Directory.GetDirectories(work.Path, "*.txold"));
    }

    /// <summary>
    /// Even if a crash happens right after the destination is moved aside to .txold, Recover continues the swap.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt and build/new.txt exist, and Move(build→site, overwrite: true) is stopped right after Committing.</para>
    /// <para>When: site is moved to site.{txid}.txold by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, site has only new.txt, and neither build nor .txold exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ContinuesSwapThatCrashedAfterBackup()
    {
        await using TempDirectory work = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "build", "new.txt"), "new");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("build", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string id = System.IO.Path.GetFileNameWithoutExtension(journal).Substring("tx-".Length);
        Directory.Move(site, site + "." + id + ".txold");

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal(new[] { "new.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "build")));
        Assert.Empty(Directory.GetDirectories(work.Path, "*.txold"));
    }

    /// <summary>
    /// Recover after the swap was done by hand treats the Adds under the source as done too, and is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: site/old.txt and an external incoming/a.txt exist, incoming is imported to site.new, and Move(site.new→site, overwrite: true) is scheduled.</para>
    /// <para>When: the commit is stopped right after the first apply (the Add), the swap is done by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, no operation is skipped, and site has only a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_TreatsAddsUnderSourceAsDoneWhenSwapIsDone()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        string staged = System.IO.Path.Combine(work.Path, "site.new");
        Directory.CreateDirectory(site);
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        string incoming = System.IO.Path.Combine(outside.Path, "incoming");
        Directory.CreateDirectory(incoming);
        await File.WriteAllTextAsync(System.IO.Path.Combine(incoming, "a.txt"), "alpha");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.ImportAsync(incoming, "site.new");
            await tx.MoveAsync("site.new", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Directory.Delete(site, recursive: true);
        Directory.Move(staged, site);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Assert.Single(report.Journals).Operations);
        Assert.Equal(new[] { "a.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
    }

    /// <summary>
    /// Even if a crash happens while a file swaps out a directory, right after the destination is moved aside to .txold, Recover continues the swap.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and a directory d with children exist, and Move(a.txt→d, overwrite: true) is stopped right after Committing.</para>
    /// <para>When: d is moved to d.{txid}.txold by hand, and RecoverAsync runs.</para>
    /// <para>Then: RolledForward, d becomes a file with the content of a.txt, and there is no .txold.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ContinuesFileSwappingOutDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "d");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "file");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(System.IO.Path.Combine(target, "old.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "d", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string id = System.IO.Path.GetFileNameWithoutExtension(journal).Substring("tx-".Length);
        Directory.Move(target, target + "." + id + ".txold");

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFileSystemEntries(work.Path, "*.txold"));
    }
}
