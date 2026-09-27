using System.IO.Compression;
using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class ExtractArchiveTests
{
    public static TheoryData<string[]> DangerousNames => new TheoryData<string[]>
    {
        new[] { "../evil.txt" },
        new[] { "a/../../evil.txt" },
        new[] { "/abs.txt" },
        new[] { "C:/drive.txt" },
        new[] { "a//b.txt" },
        new[] { "./a.txt" },
        new[] { "con.txt" },
        new[] { "a<b.txt" },
        new[] { "trail." },
        new[] { "x.txnew" },
        new[] { "a.txt", "A.TXT" },
        new[] { "a", "a/b.txt" },
        new[] { "d/", "d" },
    };

    /// <summary>
    /// Extracts nested and empty directories, and adds each file.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work folder has a ZIP with a.txt, sub/b.txt, and an empty empty/.</para>
    /// <para>When: ExtractArchiveAsync extracts it to out, then commits.</para>
    /// <para>Then: before commit there are two Adds and no real files; after commit they appear with the original content, and empty is created too.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_ExtractsNestedAndEmptyDirectoriesAndAddsFiles()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(
            System.IO.Path.Combine(work.Path, "in.zip"),
            ("a.txt", "alpha"),
            ("sub/b.txt", "beta"),
            ("empty/", null));
        string output = System.IO.Path.Combine(work.Path, "out");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExtractArchiveAsync("in.zip", "out");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, change => Assert.Equal(PendingChangeKind.Add, change.Kind));
        Assert.False(File.Exists(System.IO.Path.Combine(output, "a.txt")));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("alpha", await File.ReadAllTextAsync(System.IO.Path.Combine(output, "a.txt")));
        Assert.Equal("beta", await File.ReadAllTextAsync(System.IO.Path.Combine(output, "sub", "b.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(output, "empty")));
    }

    /// <summary>
    /// Extracted files get the entry time, and progress is reported with the total size.
    /// </summary>
    /// <remarks>
    /// <para>Given: a ZIP with two entries, hello and world!, both timed 2021-02-03 04:05:06.</para>
    /// <para>When: ExtractArchiveAsync runs with a progress receiver, then commits.</para>
    /// <para>Then: the file times match the entries, the reported total is 11 bytes, and the last report is 11 bytes.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_UsesEntryTimeAndReportsTotalSize()
    {
        await using TempDirectory work = TempDirectory.Create();
        DateTime written = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Local);
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), written, ("a.txt", "hello"), ("b.txt", "world!"));
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExtractArchiveAsync("in.zip", "out", progress: progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(written, File.GetLastWriteTime(System.IO.Path.Combine(work.Path, "out", "a.txt")));
        Assert.All(progress.Reports, report => Assert.Equal(11, report.TotalBytes));
        Assert.Equal(11, progress.Reports[^1].BytesCopied);
    }

    /// <summary>
    /// An uncommitted ZIP created in the same transaction can be extracted.
    /// </summary>
    /// <remarks>
    /// <para>Given: tree.zip is created from tree/a.txt with CreateArchiveAsync and not committed yet.</para>
    /// <para>When: tree.zip is extracted to copy with ExtractArchiveAsync, then committed.</para>
    /// <para>Then: copy/a.txt appears with the original content.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_ExtractsUncommittedZip()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateArchiveAsync("tree", "tree.zip");

        await tx.ExtractArchiveAsync("tree.zip", "copy");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("alpha", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "copy", "a.txt")));
    }

    /// <summary>
    /// A dangerous entry name fails without leaving anything.
    /// </summary>
    /// <param name="names">The ZIP entry names.</param>
    /// <remarks>
    /// <para>Given: the work folder has a ZIP with a dangerous name.</para>
    /// <para>When: ExtractArchiveAsync extracts it to out.</para>
    /// <para>Then: it throws InvalidDataException, and there is no out, no .txnew, and no pending change.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(DangerousNames))]
    public async Task ExtractArchiveAsync_DangerousEntryNameThrowsInvalidDataExceptionAndLeavesNothing(string[] names)
    {
        await using TempDirectory work = TempDirectory.Create();
        string archive = System.IO.Path.Combine(work.Path, "in.zip");
        await CreateZipAsync(archive, names.Select(name => (name, (string?)"x")).ToArray());
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => tx.ExtractArchiveAsync("in.zip", "out"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Imports an external ZIP with Shift_JIS names by specifying the encoding.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside the work folder, a ZIP whose entry is a Japanese file name written in Shift_JIS.</para>
    /// <para>When: ImportArchiveAsync runs with Shift_JIS specified, then commits.</para>
    /// <para>Then: the Japanese file name appears with the original content, and the external ZIP remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_ImportsShiftJisNamesWithEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string archive = System.IO.Path.Combine(outside.Path, "sjis.zip");
        await using (FileStream stream = File.Create(archive))
        {
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, shiftJis);

            // The Japanese name is on purpose: Shift_JIS decoding needs characters outside ASCII.
            await using StreamWriter writer = new StreamWriter(zip.CreateEntry("日本語.txt").Open());
            await writer.WriteAsync("naiyou");
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportArchiveAsync(archive, "out", shiftJis);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("naiyou", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "out", "日本語.txt")));
        Assert.True(File.Exists(archive));
    }

    /// <summary>
    /// Rejects an invalid ZIP path.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work folder has in.zip, and outside there is a directory sub.</para>
    /// <para>When: ImportArchiveAsync is called with the ZIP inside the work folder, a missing external ZIP, and the external directory, and ExtractArchiveAsync with a missing ZIP.</para>
    /// <para>Then: in order, ArgumentException, ExternalConflictException, UnsupportedOperationException, and ExternalConflictException, and out is not created.</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_RejectsInvalidZipPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string inside = System.IO.Path.Combine(work.Path, "in.zip");
        await CreateZipAsync(inside, ("a.txt", "alpha"));
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => tx.ImportArchiveAsync(inside, "out"));
        await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportArchiveAsync(System.IO.Path.Combine(outside.Path, "none.zip"), "out"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ImportArchiveAsync(directory, "out"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ExtractArchiveAsync("none.zip", "out"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Rejects an invalid destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: in.zip and an existing directory exists, and busy/new.txt is added.</para>
    /// <para>When: ExtractArchiveAsync targets exists, a path without a parent, and busy, the parent of the Add.</para>
    /// <para>Then: the first two throw ExternalConflictException, the last throws InvalidOperationException, and the pending changes stay one Add.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_RejectsInvalidDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), ("a.txt", "alpha"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "exists"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "busy"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("new"u8.ToArray()))
        {
            await tx.AddAsync(System.IO.Path.Combine("busy", "new.txt"), content);
        }

        ExternalConflictException exists = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExtractArchiveAsync("in.zip", "exists"));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExtractArchiveAsync("in.zip", System.IO.Path.Combine("missing", "out")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExtractArchiveAsync("in.zip", "busy"));

        Assert.Equal(System.IO.Path.Combine(work.Path, "exists"), exists.Path);
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), parent.Path);
        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// Cancellation and discard leave no destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: a ZIP with sub/a.txt and b.txt.</para>
    /// <para>When: an ExtractArchiveAsync is canceled at the first progress report, then the ZIP is extracted to another destination and the transaction is disposed without commit.</para>
    /// <para>Then: cancellation adds no pending change, and after Dispose neither destination nor any .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_CancelAndDiscardLeaveNoDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), ("sub/a.txt", "alpha"), ("b.txt", "beta"));
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            CancelOnReport progress = new CancelOnReport(source);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tx.ExtractArchiveAsync("in.zip", "cancelled", progress: progress, cancellationToken: source.Token));

            Assert.Empty(tx.GetPendingChanges());
            Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "cancelled")));
            await tx.ExtractArchiveAsync("in.zip", "disposed");
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "disposed")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A ZIP whose total extracted size exceeds the limit fails without staging anything.
    /// </summary>
    /// <remarks>
    /// <para>Given: the work folder has a ZIP with a 5-byte a.txt and a 4-byte b.txt.</para>
    /// <para>When: ExtractArchiveAsync runs with a limit of 8 bytes, then again with a limit of 9 bytes.</para>
    /// <para>Then: the first throws InvalidDataException and leaves no destination, no .txnew, and no operation; the second succeeds with two Adds.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_TotalOverLimitStagesNothing()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(
            System.IO.Path.Combine(work.Path, "in.zip"),
            ("a.txt", "alpha"),
            ("b.txt", "beta"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => tx.ExtractArchiveAsync("in.zip", "out", maxExtractedBytes: 8));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Empty(tx.GetPendingChanges());

        await tx.ExtractArchiveAsync("in.zip", "out", maxExtractedBytes: 9);
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// A negative limit throws ArgumentOutOfRangeException.
    /// </summary>
    /// <remarks>
    /// <para>Given: a ZIP outside the work folder.</para>
    /// <para>When: ImportArchiveAsync runs with a limit of -1.</para>
    /// <para>Then: it throws ArgumentOutOfRangeException, and there is no operation.</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_NegativeLimitThrowsArgumentOutOfRangeException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string archive = System.IO.Path.Combine(outside.Path, "in.zip");
        await CreateZipAsync(archive, ("a.txt", "alpha"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tx.ImportArchiveAsync(archive, "out", maxExtractedBytes: -1));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// For a Stored ZIP with a false Length, ExtractArchiveAsync stops when the bytes read exceed the limit, and deletes the partial output.
    /// </summary>
    /// <remarks>
    /// <para>Given: a 100-byte Stored entry whose Length in the central directory is 10.</para>
    /// <para>When: ExtractArchiveAsync runs with a limit of 50.</para>
    /// <para>Then: it throws InvalidDataException, and there is no destination, no .txnew, and no operation.</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_StoredEntryWithFalseLengthStopsAtBytesReadAndDeletesPartialOutput()
    {
        await using TempDirectory work = TempDirectory.Create();
        string archive = System.IO.Path.Combine(work.Path, "in.zip");
        await CreateStoredZipWithLiedLengthAsync(archive, actualLength: 100, declaredLength: 10);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => tx.ExtractArchiveAsync("in.zip", "out", maxExtractedBytes: 50));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Empty(tx.GetPendingChanges());
    }

    private static async Task CreateStoredZipWithLiedLengthAsync(string path, int actualLength, uint declaredLength)
    {
        byte[] payload = new byte[actualLength];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await using (FileStream stream = File.Create(path))
        {
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry entry = zip.CreateEntry("a.bin", CompressionLevel.NoCompression);
            await using Stream content = entry.Open();
            await content.WriteAsync(payload);
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        int centralDirectory = -1;
        for (int i = 0; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                centralDirectory = i;
                break;
            }
        }

        bytes[centralDirectory + 24] = (byte)declaredLength;
        bytes[centralDirectory + 25] = (byte)(declaredLength >> 8);
        bytes[centralDirectory + 26] = (byte)(declaredLength >> 16);
        bytes[centralDirectory + 27] = (byte)(declaredLength >> 24);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static Task CreateZipAsync(string path, params (string Name, string? Content)[] entries)
    {
        return CreateZipAsync(path, null, entries);
    }

    private static async Task CreateZipAsync(string path, DateTime? written, params (string Name, string? Content)[] entries)
    {
        await using FileStream stream = File.Create(path);
        using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string? content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            if (written is not null)
            {
                entry.LastWriteTime = written.Value;
            }

            if (content is null)
            {
                continue;
            }

            await using StreamWriter writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(content);
        }
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

        public void Report(TransferProgress value) => _source.Cancel();
    }
}
