using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLockTests : IDisposable
{
    public RecoverLockTests()
    {
        CrashInjector.Reset();
    }

    public void Dispose()
    {
        CrashInjector.Reset();
    }

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
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.True(File.Exists(lockFile));
        Assert.False(File.Exists(target));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(lockFile));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
    }

    /// <summary>
    /// Recover より先に取り直したロックは、Recover のあとも有効である
    /// </summary>
    /// <remarks>
    /// <para>前提: Add を AfterCommitting で止めたあと、別トランザクションが同じパスを Add している</para>
    /// <para>手順: RecoverAsync し、取り直したトランザクションが再ステージし、第三者が同じパスを Add する</para>
    /// <para>期待: 対象は止めた Add の内容になり、再ステージでき、第三者の Add は LockContentionException になる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_先に取り直したロックは閉じないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await crashed.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }

        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        Assert.True(File.Exists(lockFile));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(lockFile));
        Assert.Single(holder.GetPendingChanges());

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("again");
        await holder.UpdateAsync("a.txt", again);
        Assert.Single(holder.GetPendingChanges());

        await using ITransaction third = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream other = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => third.AddAsync("a.txt", other));
        Assert.Equal(target, contention.Path);
    }
}
