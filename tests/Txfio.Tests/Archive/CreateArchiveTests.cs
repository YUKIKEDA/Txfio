using System.IO.Compression;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class CreateArchiveTests
{
    /// <summary>
    /// A directory becomes a ZIP that is added, and it appears at the real path at commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree contains a.txt, sub/b.txt, and an empty directory empty.</para>
    /// <para>When: tree is passed to CreateArchiveAsync as out.zip, then committed.</para>
    /// <para>Then: before commit there is one Add and no out.zip; after commit the ZIP has entries a.txt, sub/b.txt, and empty/ with the original content.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_AddsDirectoryAsZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(System.IO.Path.Combine(tree, "sub"));
        Directory.CreateDirectory(System.IO.Path.Combine(tree, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "sub", "b.txt"), "beta");
        string archive = System.IO.Path.Combine(work.Path, "out.zip");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("tree", "out.zip");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.False(File.Exists(archive));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Dictionary<string, string> entries = await ReadEntriesAsync(archive);
        Assert.Equal(new[] { "a.txt", "empty/", "sub/b.txt" }, entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("alpha", entries["a.txt"]);
        Assert.Equal("beta", entries["sub/b.txt"]);
    }

    /// <summary>
    /// With includeBaseDirectory, the directory name becomes the root of the entries.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree contains a.txt, and an empty directory blank exists.</para>
    /// <para>When: tree and blank are passed to CreateArchiveAsync with includeBaseDirectory, then committed.</para>
    /// <para>Then: the tree ZIP has tree/a.txt, and the blank ZIP has one entry, blank/.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_IncludeBaseDirectoryIncludesDirectoryName()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "blank"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("tree", "tree.zip", includeBaseDirectory: true);
        await tx.CreateArchiveAsync("blank", "blank.zip", includeBaseDirectory: true);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Dictionary<string, string> tree = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "tree.zip"));
        Dictionary<string, string> blank = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "blank.zip"));
        Assert.Equal("alpha", Assert.Single(tree, pair => pair.Key == "tree/a.txt").Value);
        Assert.Equal("blank/", Assert.Single(blank).Key);
    }

    /// <summary>
    /// A file becomes one entry with the file name, with its time and progress.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt with content hello and last write time 2020-05-06 07:08:10.</para>
    /// <para>When: it is passed to CreateArchiveAsync as a.zip with includeBaseDirectory, then committed.</para>
    /// <para>Then: the only entry is a.txt with the same time as the source file, and progress reports 5 bytes with a null total.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_FileBecomesOneEntryWithFileName()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime written = new DateTime(2020, 5, 6, 7, 8, 10, DateTimeKind.Local);
        File.SetLastWriteTime(file, written);
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("a.txt", "a.zip", CompressionLevel.Fastest, includeBaseDirectory: true, progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        using ZipArchive zip = ZipFile.OpenRead(System.IO.Path.Combine(work.Path, "a.zip"));
        ZipArchiveEntry entry = Assert.Single(zip.Entries);
        Assert.Equal("a.txt", entry.FullName);
        Assert.Equal(written, entry.LastWriteTime.DateTime);
        Assert.Equal(new TransferProgress(5, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// An empty directory becomes an empty ZIP, and 0 bytes is reported once at the end.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory blank exists.</para>
    /// <para>When: blank is passed to CreateArchiveAsync, then committed.</para>
    /// <para>Then: the ZIP has no entries, and progress reports 0 bytes once.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_EmptyDirectoryBecomesEmptyZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "blank"));
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("blank", "blank.zip", progress: progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Empty(await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "blank.zip")));
        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// Rejects an invalid relation between the output and the input.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree contains a.txt, and the work folder has exists.zip.</para>
    /// <para>When: CreateArchiveAsync targets under tree, the existing ZIP, a path without a parent, and the same path as the input. A missing input is tried too.</para>
    /// <para>Then: under the input and the same path throw InvalidOperationException, the others throw ExternalConflictException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_RejectsInvalidOutput()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        string existing = System.IO.Path.Combine(work.Path, "exists.zip");
        await File.WriteAllTextAsync(existing, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync("tree", System.IO.Path.Combine("tree", "in.zip")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("tree", "tree"));
        ExternalConflictException exists = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateArchiveAsync("tree", "exists.zip"));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateArchiveAsync("tree", System.IO.Path.Combine("missing", "out.zip")));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.CreateArchiveAsync("none", "out.zip"));

        Assert.Equal(existing, exists.Path);
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Rejects when this transaction has an operation under the input or at the output.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree/new.txt is added, and out.zip is added too.</para>
    /// <para>When: CreateArchiveAsync puts tree into another ZIP, and another directory into out.zip.</para>
    /// <para>Then: both throw InvalidOperationException, and the pending changes stay two Adds.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_RejectsOperationUnderInputOrAtOutput()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "other"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("new"u8.ToArray()))
        {
            await tx.AddAsync(System.IO.Path.Combine("tree", "new.txt"), content);
        }

        await using (MemoryStream content = new MemoryStream("zip"u8.ToArray()))
        {
            await tx.AddAsync("out.zip", content);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("tree", "tree.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("other", "out.zip"));

        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// Cancellation and discard leave no ZIP.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree contains a.txt.</para>
    /// <para>When: a CreateArchiveAsync is canceled at the first progress report, then another ZIP is created and the transaction is disposed without commit.</para>
    /// <para>Then: cancellation adds no pending change, and after Dispose neither ZIP nor any .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_CancelAndDiscardLeaveNoZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            CancelOnReport progress = new CancelOnReport(source);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tx.CreateArchiveAsync("tree", "cancelled.zip", progress: progress, cancellationToken: source.Token));

            Assert.Empty(tx.GetPendingChanges());
            await tx.CreateArchiveAsync("tree", "disposed.zip");
        }

        Assert.Empty(Directory.GetFiles(work.Path));
    }

    /// <summary>
    /// A ZIP outside contains the same bytes as ReadAsync, and leaves nothing in the journal or locks.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree/a.txt is updated, and b.txt is only added with no real file.</para>
    /// <para>When: tree and b.txt are passed to ExportArchiveAsync outside the work folder.</para>
    /// <para>Then: the ZIP has the Update and Add content, the pending changes stay two, and no lock files are added.</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_PutsStagedContentInExternalZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync(System.IO.Path.Combine("tree", "a.txt"), content);
        }

        await using (MemoryStream content = new MemoryStream("staged"u8.ToArray()))
        {
            await tx.AddAsync("b.txt", content);
        }

        string lockDirectory = System.IO.Path.Combine(work.Path, ".txfio", "locks");
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;
        string treeZip = System.IO.Path.Combine(outside.Path, "tree.zip");
        string fileZip = System.IO.Path.Combine(outside.Path, "b.zip");

        await tx.ExportArchiveAsync("tree", treeZip);
        await tx.ExportArchiveAsync("b.txt", fileZip);

        Assert.Equal("updated", Assert.Single(await ReadEntriesAsync(treeZip), pair => pair.Key == "a.txt").Value);
        Assert.Equal("staged", Assert.Single(await ReadEntriesAsync(fileZip), pair => pair.Key == "b.txt").Value);
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// Rejects an external ZIP path that is occupied, has no parent, or is inside the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work folder has a.txt, and outside there are exists.zip and a directory sub.</para>
    /// <para>When: ExportArchiveAsync targets the existing file, the directory, a path without a parent, and a path inside the work folder. A missing input is tried too.</para>
    /// <para>Then: inside the work folder throws ArgumentException, the others throw ExternalConflictException, and the existing file does not change.</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_RejectsInvalidZipPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string existing = System.IO.Path.Combine(outside.Path, "exists.zip");
        await File.WriteAllTextAsync(existing, "keep");
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        string missingParent = System.IO.Path.Combine(outside.Path, "missing", "a.zip");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException file = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", existing));
        ExternalConflictException folder = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", directory));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", missingParent));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.ExportArchiveAsync("a.txt", System.IO.Path.Combine(work.Path, "in.zip")));
        await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("none.txt", System.IO.Path.Combine(outside.Path, "none.zip")));

        Assert.Equal(existing, file.Path);
        Assert.Equal(directory, folder.Path);
        Assert.Equal(System.IO.Path.GetDirectoryName(missingParent), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(System.IO.Path.Combine(outside.Path, "none.zip")));
    }

    /// <summary>
    /// An external ZIP that succeeded stays after Dispose, and a canceled one is deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work folder has a.txt.</para>
    /// <para>When: ExportArchiveAsync runs and the transaction is disposed; in another transaction the export is canceled at the first progress report.</para>
    /// <para>Then: the ZIP that succeeded remains, and the canceled ZIP does not.</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_KeepsSucceededZipAndDeletesCanceledZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string kept = System.IO.Path.Combine(outside.Path, "kept.zip");
        string cancelled = System.IO.Path.Combine(outside.Path, "cancelled.zip");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportArchiveAsync("a.txt", kept);
        }

        await using ITransaction again = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => again.ExportArchiveAsync("a.txt", cancelled, progress: progress, cancellationToken: source.Token));

        Assert.Equal("disk", Assert.Single(await ReadEntriesAsync(kept)).Value);
        Assert.NotEmpty(progress.Reports);
        Assert.False(File.Exists(cancelled));
    }

    /// <summary>
    /// The ZIP's .txnew is recorded in the journal before it is written.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree/a.txt exists.</para>
    /// <para>When: tree is passed to CreateArchiveAsync, and the journal is read on progress while writing.</para>
    /// <para>Then: at every progress report, the journal contains the .txnew of out.zip.</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_RecordsJournalBeforeTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        JournalProbeProgress probe = new JournalProbeProgress(work.Path, "out.zip.");

        await tx.CreateArchiveAsync("tree", "out.zip", progress: probe);

        Assert.True(probe.Reported);
        Assert.True(probe.AlwaysJournaled);
    }

    private static async Task<Dictionary<string, string>> ReadEntriesAsync(string archivePath)
    {
        Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using StreamReader reader = new StreamReader(entry.Open());
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    private sealed class ProgressList : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _source;

        public CancelOnReport(CancellationTokenSource source) => _source = source;

        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value)
        {
            Reports.Add(value);
            _source.Cancel();
        }
    }
}
