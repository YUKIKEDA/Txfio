using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveChainTests
{
    /// <summary>
    /// 連鎖の先頭だけ適用して落ちても、Recover が残りの Move を同じ順で終える
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2) と Move(log→log.1) を予約し、最初の適用の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: 止めた時点では log.2 だけが旧 log.1 で、Recover は RolledForward、log.1 は旧 log になり、log.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_連鎖の途中から同じ順で完了すること()
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
    /// 連鎖をすべて適用してからジャーナルを消す前に落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2) と Move(log→log.1) を予約し、最初の適用の直後に止め、2 件目を手で適用してから RecoverAsync する</para>
    /// <para>期待: RolledForward で飛ばした操作は無く、log.2 は旧 log.1、log.1 は旧 log であり、ジャーナルは消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_連鎖をすべて適用したあとならRolledForwardになること()
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
    /// ディレクトリの連鎖をすべて適用してからジャーナルを消す前に落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリ v1 と cur があり、v0 は無い</para>
    /// <para>手順: Move(v1→v0) と Move(cur→v1) を予約し、最初の適用の直後に止め、2 件目を手で適用してから RecoverAsync する</para>
    /// <para>期待: RolledForward で、v0 は旧 v1、v1 は旧 cur であり、cur は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ディレクトリの連鎖をすべて適用したあとならRolledForwardになること()
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
    /// ファイル Move の移動元へ書き直した Add まで適用してから落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、b.txt は無い</para>
    /// <para>手順: Move(a.txt→b.txt) のあと a.txt へ書き、Move の適用の直後に止め、Add の .txnew を手で a.txt へ移してから RecoverAsync する</para>
    /// <para>期待: RolledForward で、b.txt は旧 a.txt、a.txt は書いた内容であり、.txnew は残らない</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_移動元へのAddまで適用したあとならRolledForwardになること()
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
