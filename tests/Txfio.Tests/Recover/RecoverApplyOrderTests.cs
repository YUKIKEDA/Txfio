using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverApplyOrderTests
{
    /// <summary>
    /// Even if a Committing journal lists the Update before the Move, Recover moves first and then updates.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing journal with Update(B) followed by Move(A→B).</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, the destination has the Update content, and there is no source, no journal, and no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_MovesBeforeUpdateEvenWhenUpdateIsListedFirst()
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
