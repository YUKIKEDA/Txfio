using System.IO.Compression;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingNameTests
{
    private const string OtherId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    /// <summary>
    /// Names in the staging-name form are recognized, ignoring case and trailing dots and spaces.
    /// </summary>
    /// <remarks>
    /// <para>Given: a name that ends in a transaction ID and .txnew, .txnew.prev, or .txold after a non-empty name.</para>
    /// <para>When: IsStagingName is called.</para>
    /// <para>Then: it returns true.</para>
    /// </remarks>
    /// <param name="name">The name to check.</param>
    [Theory]
    [InlineData("a.txt." + OtherId + ".txnew")]
    [InlineData("a.txt." + OtherId + ".txnew.prev")]
    [InlineData("dir." + OtherId + ".txold")]
    [InlineData("A.TXT.3F2504E0-4F89-11D3-9A0C-0305E82C3301.TXNEW")]
    [InlineData("a." + OtherId + ".txnew.")]
    [InlineData("a." + OtherId + ".txold. .")]
    [InlineData(".a." + OtherId + ".txnew")]
    public void IsStagingName_MatchesStagingNames(string name)
    {
        Assert.True(global::Txfio.Txfio.IsStagingName(name));
    }

    /// <summary>
    /// Names that are not in the staging-name form are not recognized.
    /// </summary>
    /// <remarks>
    /// <para>Given: a name with no transaction ID, an empty name part, a different suffix, or a malformed ID.</para>
    /// <para>When: IsStagingName is called.</para>
    /// <para>Then: it returns false.</para>
    /// </remarks>
    /// <param name="name">The name to check.</param>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("notes.txnew")]
    [InlineData("a.txold")]
    [InlineData("." + OtherId + ".txnew")]
    [InlineData(OtherId + ".txnew")]
    [InlineData("a." + OtherId + ".txnew.bak")]
    [InlineData("a." + OtherId + ".txold.prev")]
    [InlineData("a." + OtherId + ".prev")]
    [InlineData("a.3f2504e04f8911d39a0c0305e82c3301.txnew")]
    [InlineData("a.{3f2504e0-4f89-11d3-9a0c-0305e82c3301}.txnew")]
    [InlineData("a.3f2504e0-4f89-11d3-9a0c-0305e82c330.txnew")]
    public void IsStagingName_DoesNotMatchOtherNames(string name)
    {
        Assert.False(global::Txfio.Txfio.IsStagingName(name));
    }

    /// <summary>
    /// A path instead of one name is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: a name with a backslash or a slash.</para>
    /// <para>When: IsStagingName is called.</para>
    /// <para>Then: ArgumentException.</para>
    /// </remarks>
    /// <param name="name">The name to check.</param>
    [Theory]
    [InlineData("d/a." + OtherId + ".txnew")]
    [InlineData("d\\a.txt")]
    public void IsStagingName_NameWithSeparatorThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(() => global::Txfio.Txfio.IsStagingName(name));
    }

    /// <summary>
    /// A null name is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: null.</para>
    /// <para>When: IsStagingName is called.</para>
    /// <para>Then: ArgumentNullException.</para>
    /// </remarks>
    [Fact]
    public void IsStagingName_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => global::Txfio.Txfio.IsStagingName(null!));
    }

    /// <summary>
    /// A staging name, also with a trailing dot, cannot be the target of a write.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: AddAsync is called on a staging name, with and without a trailing dot.</para>
    /// <para>Then: InvalidOperationException each time, and nothing is pending or on disk.</para>
    /// </remarks>
    /// <param name="path">The target path.</param>
    [Theory]
    [InlineData("x." + OtherId + ".txnew")]
    [InlineData("x." + OtherId + ".txnew.")]
    [InlineData("x." + OtherId + ".txnew.prev")]
    public async Task AddAsync_StagingNameThrowsInvalidOperationException(string path)
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync(path, content));

        Assert.Empty(tx.GetPendingChanges());
        Assert.Empty(Directory.GetFiles(work.Path));
    }

    /// <summary>
    /// A path with a directory segment that is a staging name cannot be written.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory d.{txid}.txold exists, as a swap backup left by a crash.</para>
    /// <para>When: WriteAllTextAsync is called on a file under it.</para>
    /// <para>Then: InvalidOperationException, and nothing is pending.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_DirectorySegmentWithStagingNameThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d." + OtherId + ".txold"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.WriteAllTextAsync("d." + OtherId + ".txold/a.txt", "x"));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Another transaction's staging file cannot be read or checked through the API.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt.{txid}.txnew of another transaction exists.</para>
    /// <para>When: ReadAsync and ExistsAsync are called on it.</para>
    /// <para>Then: InvalidOperationException for both.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAndExists_OtherTransactionStagingFileThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string staging = "a.txt." + OtherId + ".txnew";
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, staging), "uncommitted");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ReadAsync(staging));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExistsAsync(staging));
    }

    /// <summary>
    /// An Import source with a staging name is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside the work folder, a file named x.{txid}.txnew.</para>
    /// <para>When: ImportAsync is called on it.</para>
    /// <para>Then: InvalidOperationException, and nothing is pending or created.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_SourceFileWithStagingNameThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "x." + OtherId + ".txnew");
        await File.WriteAllTextAsync(source, "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ImportAsync(source, "x.txt"));

        Assert.Contains(source, ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "x.txt")));
    }

    /// <summary>
    /// An Import of a directory with a staging name under it is rejected before staging.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside the work folder, src with a.txt and sub/b.txt.{txid}.txold.</para>
    /// <para>When: ImportAsync imports src into dst.</para>
    /// <para>Then: InvalidOperationException with the staging-name path in the message; nothing is pending, dst is not created, and no .txnew is left.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DirectoryWithStagingNameUnderItThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        string reserved = System.IO.Path.Combine(source, "sub", "b.txt." + OtherId + ".txold");
        await File.WriteAllTextAsync(reserved, "b");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ImportAsync(source, "dst"));

        Assert.Contains(reserved, ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dst")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// GetEntriesAsync does not list the staging entries of other transactions.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt, and another transaction's a.txt.{txid}.txnew, a.txt.{txid}.txnew.prev, b.txt.{txid}.txold (a file), and d.{txid}.txold (a directory).</para>
    /// <para>When: GetEntriesAsync lists the work folder.</para>
    /// <para>Then: only a.txt is listed.</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_SkipsStagingEntriesOfEveryTransaction()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await WriteStagingEntriesAsync(work.Path);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        IReadOnlyList<DirectoryEntry> entries = await tx.GetEntriesAsync(".");

        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), Assert.Single(entries).Path);
    }

    /// <summary>
    /// A directory CopyAsync does not copy the staging entries of other transactions.
    /// </summary>
    /// <remarks>
    /// <para>Given: src with a.txt, and another transaction's staging entries in it (a directory .txold with a file inside).</para>
    /// <para>When: src is copied to dst and committed.</para>
    /// <para>Then: dst has only a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DirectorySkipsStagingEntriesOfEveryTransaction()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        await WriteStagingEntriesAsync(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CopyAsync("src", "dst");
        await tx.CommitAsync();

        string destination = System.IO.Path.Combine(work.Path, "dst");
        Assert.Equal(new[] { System.IO.Path.Combine(destination, "a.txt") }, Directory.GetFileSystemEntries(destination));
    }

    /// <summary>
    /// A directory ExportAsync does not copy the staging entries of other transactions.
    /// </summary>
    /// <remarks>
    /// <para>Given: src with a.txt, and another transaction's staging entries in it.</para>
    /// <para>When: src is exported outside the work folder.</para>
    /// <para>Then: the exported directory has only a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_DirectorySkipsStagingEntriesOfEveryTransaction()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        await WriteStagingEntriesAsync(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string destination = System.IO.Path.Combine(outside.Path, "out");

        await tx.ExportAsync("src", destination);

        Assert.Equal(new[] { System.IO.Path.Combine(destination, "a.txt") }, Directory.GetFileSystemEntries(destination));
    }

    /// <summary>
    /// CreateArchiveAsync does not include the staging entries of other transactions.
    /// </summary>
    /// <remarks>
    /// <para>Given: src with a.txt, and another transaction's staging entries in it.</para>
    /// <para>When: a ZIP of src is created and committed.</para>
    /// <para>Then: the ZIP has only a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_SkipsStagingEntriesOfEveryTransaction()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "a");
        await WriteStagingEntriesAsync(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("src", "a.zip");
        await tx.CommitAsync();

        using ZipArchive zip = ZipFile.OpenRead(System.IO.Path.Combine(work.Path, "a.zip"));
        Assert.Equal("a.txt", Assert.Single(zip.Entries).FullName);
    }

    /// <summary>
    /// A ZIP entry that only ends in .txnew is no longer rejected, because it is not a staging name.
    /// </summary>
    /// <remarks>
    /// <para>Given: a ZIP with notes.txnew.</para>
    /// <para>When: it is extracted into out and committed.</para>
    /// <para>Then: out/notes.txnew has the content.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_NameEndingInTxnewIsExtracted()
    {
        await using TempDirectory work = TempDirectory.Create();
        string archive = System.IO.Path.Combine(work.Path, "in.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("notes.txnew");
            await using StreamWriter writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("notes");
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExtractArchiveAsync("in.zip", "out");
        await tx.CommitAsync();

        Assert.Equal("notes", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "out", "notes.txnew")));
    }

    /// <summary>
    /// A directory delete blocked by another transaction's staging entry names it in Path.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub has b.txt.{txid}.txnew of another transaction and z.txt (neither is scheduled).</para>
    /// <para>When: DeleteAsync is called on sub.</para>
    /// <para>Then: ExternalConflictException, and Path is the staging entry (first in order of the upper-cased paths), which GetEntriesAsync does not show.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_DirectoryBlockedByStagingEntryReportsItInPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        string staging = System.IO.Path.Combine(dir, "b.txt." + OtherId + ".txnew");
        await File.WriteAllTextAsync(staging, "uncommitted");
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "z.txt"), "z");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));

        Assert.Equal(staging, ex.Path);
        Assert.Empty(tx.GetPendingChanges());
    }

    // Leaves staging entries of another transaction: a .txnew, a .txnew.prev, a file .txold, and a directory .txold with a file inside.
    private static async Task WriteStagingEntriesAsync(string directory)
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "a.txt." + OtherId + ".txnew"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "a.txt." + OtherId + ".txnew.prev"), "prev");
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "b.txt." + OtherId + ".txold"), "old");
        string oldDirectory = System.IO.Path.Combine(directory, "d." + OtherId + ".txold");
        Directory.CreateDirectory(oldDirectory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldDirectory, "inner.txt"), "inner");
    }
}
