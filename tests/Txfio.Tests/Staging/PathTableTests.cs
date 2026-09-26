namespace Txfio.Tests.Staging;

public sealed class PathTableTests
{
    /// <summary>
    /// 行を変えるメソッドを呼んだあとも、パスの位置と Move の移動先の位置が表と合う
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の表</para>
    /// <para>手順: Add、Set、Insert、Remove、RemoveAt、Load の順に行を変え、そのたびに探す</para>
    /// <para>期待: FindOperationIndex と FindMoveToIndex は、そのときの表の位置を返す</para>
    /// </remarks>
    [Fact]
    public void Find_行を変えるたびに位置が合うこと()
    {
        PathTable table = new PathTable();
        JournalOperation a = new JournalOperation(PendingChangeKind.Add, "/w/a");
        JournalOperation move = new JournalOperation(PendingChangeKind.Move, "/w/b", newPath: "/w/c");
        JournalOperation d = new JournalOperation(PendingChangeKind.Delete, "/w/d");

        table.Add(a);
        table.Add(move);
        Assert.Equal(0, table.FindOperationIndex("/w/a"));
        Assert.Equal(1, table.FindMoveToIndex("/w/c"));

        table.Set(0, d);
        Assert.Equal(-1, table.FindOperationIndex("/w/a"));
        Assert.Equal(0, table.FindOperationIndex("/w/d"));

        table.Insert(0, a);
        Assert.Equal(0, table.FindOperationIndex("/w/a"));
        Assert.Equal(2, table.FindMoveToIndex("/w/c"));

        table.Remove(move);
        Assert.Equal(-1, table.FindMoveToIndex("/w/c"));

        table.RemoveAt(0);
        Assert.Equal(-1, table.FindOperationIndex("/w/a"));
        Assert.Equal(0, table.FindOperationIndex("/w/d"));

        table.Load(new[] { move, a });
        Assert.Equal(0, table.FindMoveToIndex("/w/c"));
        Assert.Equal(1, table.FindOperationIndex("/w/a"));
        Assert.Equal(-1, table.FindOperationIndex("/w/d"));
        Assert.Equal(1, table.IndexOf(a));
    }
}
