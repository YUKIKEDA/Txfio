using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class CopyTests
{
    /// <summary>
    /// A file copy keeps the source, and the destination becomes an Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and b.txt does not.</para>
    /// <para>When: a.txt is copied to b.txt with CopyAsync.</para>
    /// <para>Then: the pending change is an Add of b.txt, a.txt remains, and b.txt does not exist yet.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FileKeepsSourceAndBecomesAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CopyAsync("a.txt", "b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(destination, pending.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    /// <summary>
    /// A directory copy creates an Add per file and empty directories.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file and an empty subdirectory.</para>
    /// <para>When: the directory is copied with CopyAsync.</para>
    /// <para>Then: one pending Add for the destination file, the empty directory exists, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DirectoryBecomesAddPerFileAndEmptyDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CopyAsync("src", "dest");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), pending.Path);
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// Dispose without commit deletes the directories the copy created.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after copying a directory with a file and an empty subdirectory.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the destination does not exist, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DisposeWithoutCommitDeletesCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CopyAsync("src", "dest");
        }

        Assert.False(Directory.Exists(destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Empty(Directory.GetFiles(source, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// This transaction's .txnew files are not copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a real file and a .txnew with this transaction's ID are in the same directory.</para>
    /// <para>When: the directory is copied with CopyAsync.</para>
    /// <para>Then: only the real file goes to the destination, and the .txnew is not copied.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DoesNotCopyThisTransactionsTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("outside");
        await tx.AddAsync("outside.txt", content);
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        string transactionId = System.IO.Path.GetFileName(journal).Substring("tx-".Length).Replace(".journal", string.Empty);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "leftover." + transactionId + ".txnew"), "skip");

        await tx.CopyAsync("src", "dest");

        string destination = System.IO.Path.Combine(work.Path, "dest");
        string copiedName = System.IO.Path.GetFileName(Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)));
        Assert.Contains("a.txt", copiedName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leftover", copiedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Junctions under it are not followed, and those entries are not copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a real file, and a junction to another directory.</para>
    /// <para>When: the directory is copied with CopyAsync.</para>
    /// <para>Then: the destination has only the real file, and nothing from the junction target.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task CopyAsync_DoesNotFollowJunctions()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string other = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await File.WriteAllTextAsync(System.IO.Path.Combine(other, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(source, "link");
        CreateJunction(link, other);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

            await tx.CopyAsync("src", "dest");

            string destination = System.IO.Path.Combine(work.Path, "dest");
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.False(File.Exists(System.IO.Path.Combine(destination, "secret.txt")));
            Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), Assert.Single(tx.GetPendingChanges()).Path);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// A source directory that is a junction cannot be used.
    /// </summary>
    /// <remarks>
    /// <para>Given: a junction to a directory that contains a file.</para>
    /// <para>When: the junction is copied with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, the destination does not exist, and the junction target remains.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task CopyAsync_JunctionSourceThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(System.IO.Path.Combine(target, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(work.Path, "link");
        CreateJunction(link, target);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tx.CopyAsync("link", "dest"));

            Assert.Contains("A reparse point cannot be used", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
            Assert.Empty(tx.GetPendingChanges());
            Assert.Equal("secret", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "secret.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// A symbolic link to a file is not copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a symbolic link to a file.</para>
    /// <para>When: the link is copied with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, the link target remains, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FileSymbolicLinkThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "target.txt");
        string link = System.IO.Path.Combine(work.Path, "link.txt");
        await File.WriteAllTextAsync(target, "secret");
        File.CreateSymbolicLink(link, target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("link.txt", "copy.txt"));

        Assert.Contains("A reparse point cannot be used", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "copy.txt")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("secret", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// The copy fails when the destination already exists.
    /// </summary>
    /// <remarks>
    /// <para>Given: source and destination files exist.</para>
    /// <para>When: CopyAsync is called.</para>
    /// <para>Then: ExternalConflictException, no pending changes, and both contents remain.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ExistingDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(destination, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CopyAsync("a.txt", "b.txt"));

        Assert.Equal(destination, conflict.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("dest", await File.ReadAllTextAsync(destination));
    }

    /// <summary>
    /// A destination without a parent fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: the source file exists, and the parent directory does not.</para>
    /// <para>When: CopyAsync targets a path under the missing parent.</para>
    /// <para>Then: ExternalConflictException, and no directory is created.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_MissingParentThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string parent = System.IO.Path.Combine(work.Path, "missing");

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CopyAsync("a.txt", "missing/b.txt"));

        Assert.Equal(parent, conflict.Path);
        Assert.False(Directory.Exists(parent));
    }

    /// <summary>
    /// A path cannot be copied to itself.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists.</para>
    /// <para>When: a.txt is copied to a.txt with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, and the file remains.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_SamePathThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("a.txt", "a.txt"));

        Assert.Contains("A path cannot be copied to itself", error.Message, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// A directory cannot be copied under itself.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory exists.</para>
    /// <para>When: CopyAsync targets a path under it.</para>
    /// <para>Then: InvalidOperationException, and nothing is created under it.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_UnderItselfThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("src", "src/nested"));

        Assert.Contains("A directory cannot be copied under itself", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(source, "nested")));
    }

    /// <summary>
    /// A file scheduled for Update cannot be copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is updated.</para>
    /// <para>When: a.txt is copied to b.txt with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, the pending change stays Update, and b.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FileScheduledForUpdateThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("a.txt", "b.txt"));

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A directory with an operation under it cannot be copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file is added under the directory.</para>
    /// <para>When: the directory is copied with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, and the destination is not created.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_OperationUnderDirectoryThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("src/a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("src", "dest"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A path under a DeleteTree cannot be copied.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory is scheduled with DeleteTree.</para>
    /// <para>When: a path under it is copied outside it with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, and the pending change stays DeleteTree.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_UnderDeleteTreeThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("tree/a.txt", "b.txt"));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// Cancellation deletes the partly created destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a file.</para>
    /// <para>When: the CopyAsync is canceled from a progress report.</para>
    /// <para>Then: OperationCanceledException, the destination does not exist, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_CancelDeletesCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.CopyAsync("src", "dest", progress, cancellation.Token));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// File copy progress uses the file length as the total.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file of 81921 bytes.</para>
    /// <para>When: CopyAsync runs with a progress receiver.</para>
    /// <para>Then: reports arrive at 81920 bytes and at the end, and the total is 81921.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_FileProgressUsesFileLengthAsTotal()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] bytes = new byte[81921];
        bytes[81920] = 1;
        await File.WriteAllBytesAsync(System.IO.Path.Combine(work.Path, "a.txt"), bytes);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("a.txt", "b.txt", progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(new TransferProgress(81920, 81921), progress.Reports[0]);
        Assert.Equal(new TransferProgress(81921, 81921), progress.Reports[1]);
    }

    /// <summary>
    /// Progress of an empty directory reports 0 bytes once.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory.</para>
    /// <para>When: CopyAsync runs with a progress receiver.</para>
    /// <para>Then: one report of 0 bytes with a null TotalBytes, and the destination directory exists.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_EmptyDirectoryReportsZeroBytesOnce()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("src", "dest", progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Directory copy progress is the total written, and the overall total is unknown.
    /// </summary>
    /// <remarks>
    /// <para>Given: two files of 2 bytes each.</para>
    /// <para>When: CopyAsync runs with a progress receiver.</para>
    /// <para>Then: two reports, every total is null, and the last reports 4 bytes written in total.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_DirectoryProgressIsWrittenTotalWithUnknownTotal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "ab");
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "b.txt"), "cd");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("src", "dest", progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.All(progress.Reports, report => Assert.Null(report.TotalBytes));
        Assert.Equal(4, progress.Reports[1].BytesCopied);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using Process process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class RecordingProgress : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnReport(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Report(TransferProgress value)
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }
}
