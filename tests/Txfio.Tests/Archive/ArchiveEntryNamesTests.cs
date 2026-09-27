namespace Txfio.Tests.Archive;

public sealed class ArchiveEntryNamesTests
{
    /// <summary>
    /// Both / and \ are separators, and a trailing separator makes a directory.
    /// </summary>
    /// <param name="fullName">The entry name.</param>
    /// <param name="expected">The split names joined with /.</param>
    /// <param name="isDirectory">Whether it is a directory entry.</param>
    /// <remarks>
    /// <para>Given: a safe entry name.</para>
    /// <para>When: Split is called.</para>
    /// <para>Then: the split names and whether it is a directory are as expected.</para>
    /// </remarks>
    [Theory]
    [InlineData("a.txt", "a.txt", false)]
    [InlineData("sub/b.txt", "sub/b.txt", false)]
    [InlineData(@"sub\b.txt", "sub/b.txt", false)]
    [InlineData("empty/", "empty", true)]
    [InlineData("données/résumé.txt", "données/résumé.txt", false)]
    [InlineData("console.txt", "console.txt", false)]
    public void Split_SplitsSafeNames(string fullName, string expected, bool isDirectory)
    {
        string[] segments = ArchiveEntryNames.Split(fullName, out bool directory);

        Assert.Equal(expected, string.Join('/', segments));
        Assert.Equal(isDirectory, directory);
    }

    /// <summary>
    /// A name that cannot be used throws InvalidDataException.
    /// </summary>
    /// <param name="fullName">The entry name.</param>
    /// <remarks>
    /// <para>Given: an entry name that leaves the destination or is not valid on Windows.</para>
    /// <para>When: Split is called.</para>
    /// <para>Then: it throws InvalidDataException.</para>
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
    public void Split_UnusableNameThrowsInvalidDataException(string fullName)
    {
        Assert.Throws<InvalidDataException>(() => ArchiveEntryNames.Split(fullName, out _));
    }
}
