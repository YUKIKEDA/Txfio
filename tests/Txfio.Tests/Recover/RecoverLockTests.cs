using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLockTests
{
    /// <summary>
    /// クラッシュ後の Recover でもロックファイルは残り、別トランザクションが同じパスを使える
    /// </summary>
    /// <remarks>
    /// <para>前提: Add を AfterCommitting で止め、Dispose している</para>
    /// <para>手順: RecoverAsync したあと、別トランザクションが同じパスを Update する</para>
    /// <para>期待: RolledForward で対象は Add の内容になり、ロックファイルは残り、Update できる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AfterCommittingのあとロックファイルが残り別トランザクションが同じパスを使えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.True(File.Exists(lockFile));
        Assert.False(File.Exists(target));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(lockFile));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
    }

    /// <summary>
    /// 落ちたトランザクションのあとで取り直したロックでは、コミットも Recover も進まない
    /// </summary>
    /// <remarks>
    /// <para>前提: holder を開始したあと、Add を AfterCommitting で止めて Dispose している。holder は別のパスを Add している</para>
    /// <para>手順: RecoverAsync し、holder で CommitAsync する。holder を Dispose してから、もう一度 RecoverAsync する</para>
    /// <para>期待: 1 回目の Recover は LockContentionException で対象は無い。コミットは RecoveryRequiredException で holder の対象も無い。2 回目は RolledForward で対象は止めた Add の内容になる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_取り直したロックがあるあいだは処理しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string held = System.IO.Path.Combine(work.Path, "b.txt");
        await using (ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            FaultInjector faults = new FaultInjector();
            faults.Arm(IFaultInjector.AfterCommitting);
            await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
            {
                await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
                await crashed.AddAsync("a.txt", content);
                await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
            }

            await using MemoryStream heldContent = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("b.txt", heldContent);

            LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
                () => global::Txfio.Txfio.RecoverAsync(work.Path));
            Assert.Equal(work.Path, contention.Path);
            Assert.False(File.Exists(target));

            await Assert.ThrowsAsync<RecoveryRequiredException>(() => holder.CommitAsync());
            Assert.False(File.Exists(held));
        }

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(held));
    }
}
