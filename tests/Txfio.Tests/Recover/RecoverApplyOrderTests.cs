using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverApplyOrderTests
{
    /// <summary>
    /// Committing で Update が Move より先に書いてあっても、Recover は Move してから Update する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の journal が Update(B) のあと Move(A→B) の順である</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で先は Update の内容、元も journal も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingでUpdateが先でもMoveしてからUpdateすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverApplyOrderFiles leftover = await LeftoverApplyOrderFiles.WriteUpdateThenMoveAsync(
            work.Path,
            committing: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("updated", await File.ReadAllTextAsync(leftover.DestPath));
    }
}
