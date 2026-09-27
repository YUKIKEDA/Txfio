using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDeleteTests
{
    /// <summary>
    /// Recover of an uncommitted Delete keeps the target.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a Delete journal and its target file remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the journal is deleted, and the target remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UncommittedDeleteKeepsTargetAndRollsBack()
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
    /// Recover finishes the leftovers of a Committing Delete.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a Committing Delete journal and its target remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and neither the target nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesCommittingDeleteAndRollsForward()
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
