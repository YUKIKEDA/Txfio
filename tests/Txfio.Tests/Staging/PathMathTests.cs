namespace Txfio.Tests.Staging;

public sealed class PathMathTests
{
    /// <summary>
    /// 配下の判定はディレクトリ自身を含めず、名前の前方一致だけのパスも含めない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリ root/a</para>
    /// <para>手順: root/a、root/a/b、root/ab を IsUnder と IsEqualOrUnder で調べる</para>
    /// <para>期待: root/a は IsEqualOrUnder だけ、root/a/b は両方、root/ab はどちらでもない</para>
    /// </remarks>
    [Fact]
    public void IsUnder_自身と前方一致だけのパスを含めないこと()
    {
        string directory = Path.Combine(Path.GetTempPath(), "root", "a");
        string child = Path.Combine(directory, "b");
        string sibling = directory + "b";

        Assert.False(PathMath.IsUnder(directory, directory));
        Assert.True(PathMath.IsEqualOrUnder(directory, directory));
        Assert.True(PathMath.IsUnder(directory, child));
        Assert.True(PathMath.IsEqualOrUnder(directory, child));
        Assert.False(PathMath.IsUnder(directory, sibling));
        Assert.False(PathMath.IsEqualOrUnder(directory, sibling));
    }

    /// <summary>
    /// ディレクトリそのものと配下を、別のディレクトリの同じ位置へ置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: from と to の 2 つのディレクトリ（from は末尾に区切り文字がある）</para>
    /// <para>手順: from 自身と from/x/y.txt を Rebase する</para>
    /// <para>期待: to と to/x/y.txt</para>
    /// </remarks>
    [Fact]
    public void Rebase_そのものと配下を移し先へ置き換えること()
    {
        string root = Path.Combine(Path.GetTempPath(), "root");
        string from = Path.Combine(root, "from");
        string to = Path.Combine(root, "to");

        Assert.Equal(to, PathMath.Rebase(from + Path.DirectorySeparatorChar, to, from + Path.DirectorySeparatorChar));
        Assert.Equal(
            Path.Combine(to, "x", "y.txt"),
            PathMath.Rebase(from + Path.DirectorySeparatorChar, to, Path.Combine(from, "x", "y.txt")));
    }

    /// <summary>
    /// 子が親より先に来るよう、深い順に並べる
    /// </summary>
    /// <remarks>
    /// <para>前提: a、a/b、a/b/c、a/d の 4 つ（浅い順）</para>
    /// <para>手順: SortDeepestFirst する</para>
    /// <para>期待: a/b/c、a/d、a/b、a の順</para>
    /// </remarks>
    [Fact]
    public void SortDeepestFirst_子を親より先に並べること()
    {
        string a = Path.Combine(Path.GetTempPath(), "a");
        string ab = Path.Combine(a, "b");
        string abc = Path.Combine(ab, "c");
        string ad = Path.Combine(a, "d");
        List<string> paths = new List<string> { a, ab, abc, ad };

        PathMath.SortDeepestFirst(paths);

        Assert.Equal(new[] { abc, ad, ab, a }, paths);
    }
}
