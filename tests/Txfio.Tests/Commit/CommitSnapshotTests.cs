using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitSnapshotTests
{
    /// <summary>
    /// Move のあとに Update を投影すると、Update の Before は移動元のファイル状態になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元ファイルと、移動先への Update 用 .txnew がある</para>
    /// <para>手順: Update を先に並べた操作を TryStamp する</para>
    /// <para>期待: Update の Before は移動元と一致し、After は .txnew と一致する</para>
    /// </remarks>
    [Fact]
    public async Task TryStamp_MoveのあとUpdateのBeforeは移動元の状態であること()
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
