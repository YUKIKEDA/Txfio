using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverStagingTests
{
    /// <summary>
    /// 未コミット残骸の Recover は .txnew を消し、対象は作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Add の journal と .txnew が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で .txnew も journal も消え、対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのtxnewを削除してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// Committing 残骸の Recover は .txnew を対象へ Move する
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Committing の journal と .txnew が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で対象に内容があり、.txnew も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのtxnewをMoveしてRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// Committing の適用に失敗したら ConflictDetected を 1 回だけ返し、ジャーナルと .txnew を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Add 残骸があり、対象パスは外部が既に作っている</para>
    /// <para>手順: RecoverAsync を 2 回呼ぶ</para>
    /// <para>期待: 1 回目は ConflictDetected で、journal と .txnew は消え、対象は外部の内容のまま。2 回目は NoPendingTransactions</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingの適用に失敗するとConflictDetectedになりjournalを消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.TargetPath, "external");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("external", await File.ReadAllTextAsync(leftover.TargetPath));
        Assert.Equal(RecoverResult.NoPendingTransactions, await global::Txfio.Txfio.RecoverAsync(work.Path));
    }
}
