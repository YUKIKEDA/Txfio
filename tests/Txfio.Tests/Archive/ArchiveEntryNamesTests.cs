namespace Txfio.Tests.Archive;

public sealed class ArchiveEntryNamesTests
{
    /// <summary>
    /// 区切りは / と \ の両方で、末尾の区切りはディレクトリになる
    /// </summary>
    /// <param name="fullName">エントリ名</param>
    /// <param name="expected">区切った名前を / でつないだもの</param>
    /// <param name="isDirectory">ディレクトリエントリか</param>
    /// <remarks>
    /// <para>前提: 安全なエントリ名がある</para>
    /// <para>手順: Split する</para>
    /// <para>期待: 区切った名前とディレクトリかどうかが期待どおりになる</para>
    /// </remarks>
    [Theory]
    [InlineData("a.txt", "a.txt", false)]
    [InlineData("sub/b.txt", "sub/b.txt", false)]
    [InlineData(@"sub\b.txt", "sub/b.txt", false)]
    [InlineData("empty/", "empty", true)]
    [InlineData("日本語/ファイル.txt", "日本語/ファイル.txt", false)]
    [InlineData("console.txt", "console.txt", false)]
    public void Split_安全な名前を区切ること(string fullName, string expected, bool isDirectory)
    {
        string[] segments = ArchiveEntryNames.Split(fullName, out bool directory);

        Assert.Equal(expected, string.Join('/', segments));
        Assert.Equal(isDirectory, directory);
    }

    /// <summary>
    /// 使えない名前は InvalidDataException になる
    /// </summary>
    /// <param name="fullName">エントリ名</param>
    /// <remarks>
    /// <para>前提: 展開先の外へ出る、または Windows で使えないエントリ名がある</para>
    /// <para>手順: Split する</para>
    /// <para>期待: InvalidDataException になる</para>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"\\server\share.txt")]
    [InlineData("a:b.txt")]
    [InlineData("NUL")]
    [InlineData("lpt1.log")]
    [InlineData("space ")]
    [InlineData("a\u0001.txt")]
    [InlineData("dir.TXNEW/a.txt")]
    public void Split_使えない名前はInvalidDataExceptionになること(string fullName)
    {
        Assert.Throws<InvalidDataException>(() => ArchiveEntryNames.Split(fullName, out _));
    }
}
