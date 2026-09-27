namespace Txfio.Tests.Staging;

public sealed class PathTableTests
{
    /// <summary>
    /// After each method that changes rows, the positions of paths and Move destinations match the table.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty table.</para>
    /// <para>When: rows are changed with Add, Set, Insert, Remove, RemoveAt, and Load in order, searching after each.</para>
    /// <para>Then: FindOperationIndex and FindMoveToIndex return the positions in the table at that time.</para>
    /// </remarks>
    [Fact]
    public void Find_PositionsMatchAfterEachChange()
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
