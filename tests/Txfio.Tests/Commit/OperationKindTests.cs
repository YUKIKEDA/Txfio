namespace Txfio.Tests.Commit;

public sealed class OperationKindTests
{
    /// <summary>
    /// すべての操作種別に実装があり、未知の種別は黙って飛ばさず例外を投げる
    /// </summary>
    /// <remarks>
    /// <para>前提: PendingChangeKind の定義済みの値と、定義に無い値 99</para>
    /// <para>手順: それぞれで OperationKind.For を呼ぶ</para>
    /// <para>期待: 定義済みの値はその種別の実装を返し、99 は InvalidOperationException を投げる</para>
    /// </remarks>
    [Fact]
    public void For_定義済みは実装を返し未知の種別は例外を投げること()
    {
        foreach (PendingChangeKind kind in Enum.GetValues<PendingChangeKind>())
        {
            Assert.Equal(kind, OperationKind.For(kind).Kind);
        }

        Assert.Throws<InvalidOperationException>(() => OperationKind.For((PendingChangeKind)99));
    }
}
