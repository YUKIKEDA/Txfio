namespace Txfio.Tests;

public sealed class TxfioTests
{
    /// <summary>
    /// ライブラリアセンブリの名前は Txfio である
    /// </summary>
    /// <remarks>
    /// <para>前提: テストが Txfio を参照している</para>
    /// <para>手順: 公開型 Txfio のアセンブリ名を読む</para>
    /// <para>期待: 名前が Txfio である</para>
    /// </remarks>
    [Fact]
    public void アセンブリ名がTxfioであること()
    {
        Assert.Equal("Txfio", typeof(global::Txfio.Txfio).Assembly.GetName().Name);
    }
}
