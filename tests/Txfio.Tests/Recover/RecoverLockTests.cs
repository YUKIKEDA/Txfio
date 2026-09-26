using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLockTests
{
    /// <summary>
    /// クラッシュ後の Recover はロックファイルを消し、別トランザクションは同じパスを使える
    /// </summary>
    /// <remarks>
    /// <para>前提: Add を AfterCommitting で止め、Dispose している</para>
    /// <para>手順: RecoverAsync したあと、別トランザクションが同じパスを Update する</para>
    /// <para>期待: RolledForward で対象は Add の内容になり、ロックファイルは消え、Update できる（ロックファイルは作り直される）</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AfterCommittingのあとロックファイルを消し別トランザクションが同じパスを使えること()
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
        Assert.False(File.Exists(lockFile));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
        Assert.True(File.Exists(lockFile));
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

    /// <summary>
    /// Recover は、過去の操作が残したパスと意図のロックファイルを消し、生存ロックとジャーナルは残す
    /// </summary>
    /// <remarks>
    /// <para>前提: sub/a.txt を Add してコミットし、ロックファイルが残っている。別のトランザクションが生きている</para>
    /// <para>手順: 生きているトランザクションを破棄してから RecoverAsync する</para>
    /// <para>期待: .txfio/locks に .lock は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_残ったロックファイルを消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.WriteAllTextAsync("sub/a.txt", "a");
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        string locks = MetadataNames.LockFolderPath(work.Path);
        Assert.NotEmpty(Directory.GetFiles(locks, "*.lock"));

        await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Empty(Directory.GetFiles(locks, "*.lock"));
    }

    /// <summary>
    /// 生きているトランザクションがロックを持っているあいだは、Recover は何もしないのでロックファイルも残る
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションが a.txt を Add している</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: LockContentionException で、a.txt のロックファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_生きているトランザクションがいるとロックファイルを消さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await holder.WriteAllTextAsync("a.txt", "a");
        string lockFile = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt"));

        await Assert.ThrowsAsync<LockContentionException>(() => global::Txfio.Txfio.RecoverAsync(work.Path));

        Assert.True(File.Exists(lockFile));
    }
}
