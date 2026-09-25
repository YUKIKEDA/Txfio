using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingApplierTests
{
    /// <summary>
    /// ジャーナルでは Update が先でも、適用は Move してから Update する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元ファイルと、移動先への Update 用 .txnew がある</para>
    /// <para>手順: Update を先に並べた操作一覧を TryApplyAll する</para>
    /// <para>期待: 先は Update の内容で、元も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task TryApplyAll_ジャーナルではUpdateが先でもMoveしてからUpdateすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        string staging = System.IO.Path.Combine(
            work.Path,
            "b.txt." + Guid.NewGuid().ToString("D") + ".txnew");
        await File.WriteAllTextAsync(staging, "updated");
        PathState sourceState = PathState.Capture(source);
        PathState updated = PathState.Capture(staging);

        JournalOperation[] operations =
        {
            new JournalOperation(
                PendingChangeKind.Update,
                dest,
                staging,
                before: sourceState,
                after: updated),
            new JournalOperation(
                PendingChangeKind.Move,
                source,
                newPath: dest,
                before: sourceState,
                after: PathState.Absent,
                destBefore: PathState.Absent,
                destAfter: sourceState),
        };

        Assert.True(StagingApplier.TryApplyAll(operations, out _));
        Assert.False(File.Exists(source));
        Assert.Equal("updated", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(staging));
    }
}
