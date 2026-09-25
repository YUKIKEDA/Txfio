using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDeleteTests
{
    /// <summary>
    /// 未コミット Delete の Recover は対象を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Delete の journal と対象ファイルが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのDeleteは対象を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDeleteFiles leftover = await LeftoverDeleteFiles.WriteDeleteAsync(
            work.Path,
            committing: false,
            "a.txt",
            "keep");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// Committing の Delete 残骸は Recover が削除を完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Committing の Delete journal と対象が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で対象も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのDeleteを完了してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDeleteFiles leftover = await LeftoverDeleteFiles.WriteDeleteAsync(
            work.Path,
            committing: true,
            "a.txt",
            "gone");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.TargetPath));
    }
}
