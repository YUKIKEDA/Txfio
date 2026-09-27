using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CrashInjectionTests
{
    /// <summary>
    /// Even when stopped at AfterCommitting, Recover finishes the Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is staged.</para>
    /// <para>When: CommitAsync is stopped at AfterCommitting, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop the target does not exist and the .txnew remains. After Recover the target has the content, and there is no journal and no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_RecoverFinishesAddStoppedAtAfterCommitting()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Even when an Add is stopped at AfterApply, the content remains after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add is staged.</para>
    /// <para>When: CommitAsync is stopped at AfterApply, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop the target has the Add content, and after Recover there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddStoppedAtAfterApplyKeepsContentAfterRecover()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Even when an Update is stopped at AfterApply, the content remains after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: CommitAsync is stopped at AfterApply, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop the target has the Update content, and after Recover there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateStoppedAtAfterApplyKeepsContentAfterRecover()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Even when a file Delete is stopped at AfterApply, it stays deleted after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is deleted.</para>
    /// <para>When: CommitAsync is stopped at AfterApply, then RecoverAsync runs.</para>
    /// <para>Then: the target does not exist right after the stop or after Recover. After Recover there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteStoppedAtAfterApplyStaysDeletedAfterRecover()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "gone");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.DeleteAsync("a.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(target));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Even when a Move is stopped at AfterApply, it stays moved after Recover.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file is moved.</para>
    /// <para>When: CommitAsync is stopped at AfterApply, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop the destination has the content and the source does not exist. After Recover there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveStoppedAtAfterApplyStaysMovedAfterRecover()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Even when stopped at the first AfterApply, Recover finishes the remaining operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add and a Delete of another file.</para>
    /// <para>When: CommitAsync is stopped at AfterApply, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop only the Add is applied. After Recover the Delete is applied too, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_RecoverFinishesRemainingAfterFirstAfterApplyStop()
    {
        await using TempDirectory work = TempDirectory.Create();
        string added = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(deleted, "keep");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("added");
            await tx.AddAsync("a.txt", content);
            await tx.DeleteAsync("b.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("added", await File.ReadAllTextAsync(added));
        Assert.Equal("keep", await File.ReadAllTextAsync(deleted));
        Assert.Single(Journals(work.Path));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("added", await File.ReadAllTextAsync(added));
        Assert.False(File.Exists(deleted));
        Assert.Empty(Journals(work.Path));
    }

    private static string[] Journals(string workFolder)
    {
        return Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal");
    }
}
