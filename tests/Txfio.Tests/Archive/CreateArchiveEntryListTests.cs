using System.IO.Compression;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class CreateArchiveEntryListTests
{
    /// <summary>
    /// Adds entries under the given names, in list order.
    /// </summary>
    /// <remarks>
    /// <para>Given: reports/x.csv and a.txt exist.</para>
    /// <para>When: CreateArchiveAsync pairs reports/x.csv with m/09.csv and a.txt with no name, then commits.</para>
    /// <para>Then: the entries are m/09.csv then a.txt, their content matches the source files, and the pending changes were one Add.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_AddsEntriesUnderGivenNamesInListOrder()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "reports"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "reports", "x.csv"), "csv");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[]
            {
                new ArchiveEntrySource(System.IO.Path.Combine("reports", "x.csv"), "m/09.csv"),
                new ArchiveEntrySource("a.txt"),
            },
            "out.zip");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        List<(string Name, string Content)> entries = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "out.zip"));
        Assert.Equal(new[] { ("m/09.csv", "csv"), ("a.txt", "alpha") }, entries);
    }

    /// <summary>
    /// An omitted name becomes the relative path, and an empty string for a directory puts it at the root.
    /// </summary>
    /// <remarks>
    /// <para>Given: reports/x.csv, and tree with a.txt and an empty directory empty.</para>
    /// <para>When: CreateArchiveAsync pairs reports/x.csv with no name, tree with no name, tree with an empty string, and tree with data\sub, then commits.</para>
    /// <para>Then: the ZIP contains reports/x.csv, tree/a.txt, tree/empty/, a.txt, empty/, data/sub/a.txt, and data/sub/empty/.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_OmittedNameIsRelativePathAndEmptyNameIsRoot()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "reports"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree", "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "reports", "x.csv"), "csv");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[]
            {
                new ArchiveEntrySource(System.IO.Path.Combine("reports", "x.csv")),
                new ArchiveEntrySource("tree"),
                new ArchiveEntrySource("tree", string.Empty),
                new ArchiveEntrySource("tree", @"data\sub"),
            },
            "out.zip");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        List<(string Name, string Content)> entries = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "out.zip"));
        Assert.Equal(
            new[] { "a.txt", "data/sub/a.txt", "data/sub/empty/", "empty/", "reports/x.csv", "tree/a.txt", "tree/empty/" },
            entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The same source can be added twice under different names, and an empty list makes an empty ZIP.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: a ZIP pairing a.txt with one.txt and two.txt, and a ZIP from an empty list, are created and committed.</para>
    /// <para>Then: the first has two entries with the same content, the second has no entries, and the empty one reports 0 bytes once.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_AllowsSameSourceTwiceAndEmptyList()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[] { new ArchiveEntrySource("a.txt", "one.txt"), new ArchiveEntrySource("a.txt", "two.txt") },
            "twice.zip");
        await tx.CreateArchiveAsync(Array.Empty<ArchiveEntrySource>(), "empty.zip", progress: progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(
            new[] { ("one.txt", "alpha"), ("two.txt", "alpha") },
            await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "twice.zip")));
        Assert.Empty(await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "empty.zip")));
        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// An invalid entry name throws ArgumentException, leaving no lock and no ZIP.
    /// </summary>
    /// <param name="entryName">The entry name given to the file.</param>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: CreateArchiveAsync is called with an invalid name.</para>
    /// <para>Then: it throws ArgumentException, and there are no pending changes, no ZIP, no .txnew, and no lock files.</para>
    /// </remarks>
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("con.txt")]
    [InlineData("a:b.txt")]
    [InlineData("x.txnew")]
    [InlineData("")]
    [InlineData("dir/")]
    public async Task CreateArchiveAsync_InvalidEntryNameThrowsArgumentException(string entryName)
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt", entryName) }, "out.zip"));

        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "out.zip")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        string locks = System.IO.Path.Combine(work.Path, ".txfio", "locks");
        Assert.True(!Directory.Exists(locks) || Directory.GetFiles(locks).Length == 0);
    }

    /// <summary>
    /// When names produced by walking a directory collide, it throws ArgumentException without leaving a ZIP.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree with a.txt, and b.txt exist.</para>
    /// <para>When: tree is added with an empty string (the root), and b.txt as A.TXT, in the same ZIP.</para>
    /// <para>Then: it throws ArgumentException, and there are no pending changes, no ZIP, and no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_DuplicateWalkedNameThrowsArgumentException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.CreateArchiveAsync(
                new[] { new ArchiveEntrySource("tree", string.Empty), new ArchiveEntrySource("b.txt", "A.TXT") },
                "out.zip"));

        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "out.zip")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Rejects null, staged elements, and a ZIP under an element.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is updated, and b.txt and a directory tree exist.</para>
    /// <para>When: a null sequence, a null element, a list containing a.txt, and a ZIP under tree are tried.</para>
    /// <para>Then: in order, two ArgumentNullExceptions and two InvalidOperationExceptions, and the pending changes stay one Update.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_RejectsNullStagedAndZipUnderElement()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync("a.txt", content);
        }

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tx.CreateArchiveAsync((IEnumerable<ArchiveEntrySource>)null!, "out.zip"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tx.CreateArchiveAsync(new ArchiveEntrySource[] { null! }, "out.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync(new[] { new ArchiveEntrySource("b.txt"), new ArchiveEntrySource("a.txt") }, "out.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync(
                new[] { new ArchiveEntrySource("b.txt"), new ArchiveEntrySource("tree") },
                System.IO.Path.Combine("tree", "in.zip")));

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A ZIP outside contains the same bytes as ReadAsync under the given names.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is updated, and b.txt is only added with no real file.</para>
    /// <para>When: ExportArchiveAsync pairs a.txt with x.txt and b.txt with no name, outside the work folder.</para>
    /// <para>Then: x.txt in the ZIP has the Update content, b.txt has the Add content, and the pending changes stay two.</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_AddsStagedContentUnderGivenNames()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync("a.txt", content);
        }

        await using (MemoryStream content = new MemoryStream("staged"u8.ToArray()))
        {
            await tx.AddAsync("b.txt", content);
        }

        string archive = System.IO.Path.Combine(outside.Path, "out.zip");

        await tx.ExportArchiveAsync(
            new[] { new ArchiveEntrySource("a.txt", "x.txt"), new ArchiveEntrySource("b.txt") },
            archive);

        Assert.Equal(new[] { ("x.txt", "updated"), ("b.txt", "staged") }, await ReadEntriesAsync(archive));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// A ZIP outside is not left behind when a name is invalid either.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt and b.txt exist.</para>
    /// <para>When: ExportArchiveAsync adds both as same.txt.</para>
    /// <para>Then: it throws ArgumentException, and there is no ZIP outside.</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_DuplicateNameThrowsArgumentExceptionAndLeavesNoZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string archive = System.IO.Path.Combine(outside.Path, "out.zip");

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.ExportArchiveAsync(
                new[] { new ArchiveEntrySource("a.txt", "same.txt"), new ArchiveEntrySource("b.txt", "same.txt") },
                archive));

        Assert.False(File.Exists(archive));
    }

    private static async Task<List<(string Name, string Content)>> ReadEntriesAsync(string archivePath)
    {
        List<(string Name, string Content)> entries = new List<(string Name, string Content)>();
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using StreamReader reader = new StreamReader(entry.Open());
            entries.Add((entry.FullName, await reader.ReadToEndAsync()));
        }

        return entries;
    }

    private sealed class ProgressList : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }
}
