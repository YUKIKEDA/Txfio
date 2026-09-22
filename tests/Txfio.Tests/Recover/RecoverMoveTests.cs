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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.Equal("done", await File.ReadAllTextAsync(leftover.DestPath));
    }
}
