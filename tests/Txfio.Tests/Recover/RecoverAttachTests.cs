using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverAttachTests
{
    /// <summary>
    /// 未コミット Attach の Recover は対象を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Attach の journal と対象ファイルが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのAttachは対象を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAttachFiles leftover = await LeftoverAttachFiles.WriteAttachAsync(
            work.Path,
            committing: false,
            "a.txt",
            "keep");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// Committing の Attach 残骸は期待どおりなら完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Committing の Attach journal と一致する対象がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で journal は無く、対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのAttachが一致すればRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAttachFiles leftover = await LeftoverAttachFiles.WriteAttachAsync(
            work.Path,
            committing: true,
            "a.txt",
            "keep");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// Committing で期待状態が崩れていれば journal を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Attach journal があり、対象の内容が変わっている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected で journal は残り、対象は外部の内容のまま</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのAttachが崩れているとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAttachFiles leftover = await LeftoverAttachFiles.WriteAttachAsync(
            work.Path,
            committing: true,
            "a.txt",
            "old");
        await File.WriteAllTextAsync(leftover.TargetPath, "external");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.Equal("external", await File.ReadAllTextAsync(leftover.TargetPath));
    }
}
