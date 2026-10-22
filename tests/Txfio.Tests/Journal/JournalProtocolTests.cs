using Txfio.Tests.Support;

namespace Txfio.Tests.Journal;

public sealed class JournalProtocolTests
{
    /// <summary>
    /// The first line of a journal records the protocol version.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: its journal is read.</para>
    /// <para>Then: the first line has "protocol":1.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_WritesProtocolOnFirstLine()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);

        string journal = Directory.GetFiles(MetadataNames.FolderPath(work.Path), "tx-*.journal").Single();
        string firstLine = File.ReadLines(journal).First();

        Assert.Contains("\"protocol\":1", firstLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// A journal of another protocol is unreadable, and nothing is touched.
    /// </summary>
    /// <remarks>
    /// <para>Given: the protocol of a committing Add journal of a.txt is rewritten to 2, and its .txnew exists.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, the journal and the .txnew remain, and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AnotherProtocolTouchesNothingAndReturnsJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "a.txt", "staged");
        await SetProtocolAsync(leftover.JournalPath, "2");

        RecoverReport result = await TxFileIO.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// A journal of another protocol blocks staging in the whole work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: the protocol of an Add journal of a.txt is rewritten to 2.</para>
    /// <para>When: a transaction adds b.txt.</para>
    /// <para>Then: RecoveryRequiredException with Path set to the work folder.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_RejectsWhileJournalOfAnotherProtocolExists()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "a.txt", "staged");
        await SetProtocolAsync(leftover.JournalPath, "2");
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("b");

        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => tx.AddAsync("b.txt", content));

        Assert.Equal(work.Path, required.Path);
    }

    /// <summary>
    /// A journal without protocol, as 0.2.0 writes it, is read as protocol 1.
    /// </summary>
    /// <remarks>
    /// <para>Given: a committing Add journal of a.txt without the protocol field, as 0.2.0 writes it.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and a.txt has the staged content.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ReadsJournalWithoutProtocolAsProtocolOne()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "a.txt", "staged");
        Assert.DoesNotContain("protocol", await File.ReadAllTextAsync(leftover.JournalPath), StringComparison.Ordinal);

        RecoverReport result = await TxFileIO.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// A journal with a null protocol is read as protocol 1.
    /// </summary>
    /// <remarks>
    /// <para>Given: the protocol of a committing Add journal of a.txt is rewritten to null.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and a.txt has the staged content.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ReadsJournalWithNullProtocolAsProtocolOne()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "a.txt", "staged");
        await SetProtocolAsync(leftover.JournalPath, "null");

        RecoverReport result = await TxFileIO.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    // The helper writes the journal as 0.2.0 does, without protocol; add it after the version.
    private static async Task SetProtocolAsync(string journalPath, string value)
    {
        string json = await File.ReadAllTextAsync(journalPath);
        Assert.StartsWith("{\"version\":1,", json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            journalPath,
            json.Replace("{\"version\":1,", "{\"version\":1,\"protocol\":" + value + ",", StringComparison.Ordinal));
    }
}
