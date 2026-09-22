using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDirectoryDeleteTests
{
    /// <summary>
    /// 未コミットのディレクトリ Delete の Recover は対象を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、ディレクトリ Delete の journal と空ディレクトリが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのディレクトリDeleteは対象を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: false,
            "sub");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.True(Directory.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// Committing の空ディレクトリ Delete は Recover が削除を完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing のディレクトリ Delete journal と空ディレクトリがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward でディレクトリも journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingの空ディレクトリDeleteを完了してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: true,
            "sub");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(Directory.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// Committing で直下が空でなければ journal を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing のディレクトリ Delete journal があり、直下にファイルがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected で journal とディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingのディレクトリに子があるとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: true,
            "sub");
        await File.WriteAllTextAsync(System.IO.Path.Combine(leftover.TargetPath, "external.txt"), "no");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(Directory.Exists(leftover.TargetPath));
    }
}
