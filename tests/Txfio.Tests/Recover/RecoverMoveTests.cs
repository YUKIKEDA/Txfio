using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveTests
{
    /// <summary>
    /// 未コミット Move の Recover は元を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Move の journal と元ファイルが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのMoveは元を残してRolledBackになること()
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
    /// Committing の Move 残骸は Recover が移動を完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Committing の Move journal と元が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で先の内容があり、元も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのMoveを完了してRolledForwardになること()
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
    /// Committing で既に移動済みなら Recover は完了扱いで journal を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Move journal があり、元は無く先だけある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で journal は無く先は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_既に移動済みのCommittingはRolledForwardになること()
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
    /// 置き換えの Move を Committing の直後に止めても、Recover が置き換えを終える
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、Move(a.txt→b.txt, overwrite: true) を予約した</para>
    /// <para>手順: Committing の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: RolledForward で、b.txt は旧 a.txt の中身、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_置き換えのMoveを完了すること()
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
    /// 置き換えの Move を適用し終えてから落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、Move(a.txt→b.txt, overwrite: true) を予約した</para>
    /// <para>手順: 適用の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: RolledForward で飛ばした操作は無く、b.txt は旧 a.txt の中身</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_適用済みの置き換えのMoveはRolledForwardになること()
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
}
