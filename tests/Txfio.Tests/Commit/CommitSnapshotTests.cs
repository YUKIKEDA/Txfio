using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitSnapshotTests
{
    /// <summary>
    /// When an Update is projected after a Move, the Update's Before is the state of the source file.
    /// </summary>
    /// <remarks>
    /// <para>Given: a source file, and a .txnew for an Update at the destination.</para>
    /// <para>When: TryStamp runs on operations with the Update listed first.</para>
    /// <para>Then: the Update's Before matches the source, and its After matches the .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task TryStamp_UpdateBeforeAfterMoveIsSourceState()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        string staging = System.IO.Path.Combine(work.Path, "b.txt." + Guid.NewGuid().ToString("D") + ".txnew");
        await File.WriteAllTextAsync(staging, "updated");
        PathState sourceState = PathState.Capture(source);
        JournalOperation[] operations =
        {
            new JournalOperation(PendingChangeKind.Update, dest, staging),
            new JournalOperation(PendingChangeKind.Move, source, newPath: dest),
        };

        Assert.True(OperationOutcomes.TryStamp(operations, Guid.NewGuid(), out JournalOperation[] stamped, out _));
        Assert.True(sourceState.SameAs(stamped[0].Before!));
        Assert.True(PathState.Capture(staging).SameAs(stamped[0].After!));
        Assert.True(sourceState.SameAs(stamped[1].DestAfter!));
        Assert.True(PathState.Absent.SameAs(stamped[1].After!));
    }
}
