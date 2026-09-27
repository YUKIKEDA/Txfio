using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Transfer;

public sealed class DirectoryImportExportTests
{
    /// <summary>
    /// An external directory creates an Add per file and empty directories.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside there is a file and an empty subdirectory.</para>
    /// <para>When: ImportAsync, then commit.</para>
    /// <para>Then: Succeeded, the destination has the file and the empty directory, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DirectoryBecomesAddPerFileAndKeepsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "imported");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportAsync(source, "dest");

        string destination = System.IO.Path.Combine(work.Path, "dest");
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), pending.Path);
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("imported", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal("imported", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
    }

    /// <summary>
    /// Dispose without commit deletes the directories the import created.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after an external directory is imported.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: the destination does not exist, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DisposeWithoutCommitDeletesCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ImportAsync(source, "dest");
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// A directory Import reserves the destination, and lets unrelated paths pass.
    /// </summary>
    /// <remarks>
    /// <para>Given: an external directory, and another file in the work folder.</para>
    /// <para>When: the directory is imported, then another transaction deletes that file and adds a file under the destination.</para>
    /// <para>Then: the other file can be deleted, and the Add under the destination throws LockContentionException with Path set to the destination.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DirectoryReservesOnlyDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        string dest = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ImportAsync(source, "dest");

        await second.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("dest/more.txt", content));

        Assert.Equal(dest, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A directory that contains the work folder cannot be imported under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: the parent directory of the work folder.</para>
    /// <para>When: that parent is imported under the work folder with ImportAsync.</para>
    /// <para>Then: InvalidOperationException, and the destination is not created.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DestinationUnderSourceThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string parent = System.IO.Path.GetDirectoryName(work.Path)!;
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.ImportAsync(parent, "nested"));

        Assert.Contains("A directory cannot be copied under itself", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "nested")));
    }

    /// <summary>
    /// An import fails when the destination already exists or has no parent.
    /// </summary>
    /// <remarks>
    /// <para>Given: an external directory, and an existing directory in the work folder.</para>
    /// <para>When: ImportAsync targets the existing directory and a path without a parent.</para>
    /// <para>Then: both throw ExternalConflictException, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_InvalidDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        string existing = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(existing);
        string parent = System.IO.Path.Combine(work.Path, "missing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException occupied = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(source, "dest"));
        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(source, "missing/dest"));

        Assert.Equal(existing, occupied.Path);
        Assert.Equal(parent, missing.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// A directory cannot be imported under a DeleteTree.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory is scheduled with DeleteTree, and another directory exists outside.</para>
    /// <para>When: ImportAsync targets a path under the DeleteTree.</para>
    /// <para>Then: InvalidOperationException, and the pending change stays DeleteTree.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_UnderDeleteTreeThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ImportAsync(source, "tree/imported"));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "tree", "imported")));
    }

    /// <summary>
    /// Junctions under it are not imported.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside there is a real file and a junction to another directory.</para>
    /// <para>When: the directory is imported with ImportAsync.</para>
    /// <para>Then: the destination has only the real file, and nothing from the junction target.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task ImportAsync_DoesNotFollowJunctions()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        string other = System.IO.Path.Combine(outside.Path, "other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await File.WriteAllTextAsync(System.IO.Path.Combine(other, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(source, "link");
        CreateJunction(link, other);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await tx.ImportAsync(source, "dest");

            string destination = System.IO.Path.Combine(work.Path, "dest");
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), Assert.Single(tx.GetPendingChanges()).Path);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// A symbolic link to a file cannot be imported.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside there is a symbolic link to a file.</para>
    /// <para>When: the link is imported with ImportAsync.</para>
    /// <para>Then: InvalidOperationException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_FileSymbolicLinkThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string target = System.IO.Path.Combine(outside.Path, "target.txt");
        string link = System.IO.Path.Combine(outside.Path, "link.txt");
        await File.WriteAllTextAsync(target, "secret");
        File.CreateSymbolicLink(link, target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.ImportAsync(link, "a.txt"));

        Assert.Contains("A symbolic link cannot be copied", error.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("secret", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Importing an empty directory reports 0 bytes once.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory outside.</para>
    /// <para>When: ImportAsync runs with a progress receiver.</para>
    /// <para>Then: one report of 0 bytes with a null TotalBytes, and the destination directory exists.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_EmptyDirectoryReportsZeroBytesOnce()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.ImportAsync(source, "dest", progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Canceling an import deletes what was partly created.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a file outside.</para>
    /// <para>When: the ImportAsync is canceled from a progress report.</para>
    /// <para>Then: OperationCanceledException, the destination does not exist, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_CancelDeletesCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.ImportAsync(source, "dest", progress, cancellation.Token));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// A directory Export copies the contents outside, and does not change the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file and an empty subdirectory.</para>
    /// <para>When: ExportAsync is called.</para>
    /// <para>Then: outside has the file and the empty directory, the work folder is unchanged, there are no pending changes, and no locks.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_CopiesDirectoryOutsideWithoutChangingWorkFolder()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExportAsync("src", destination);

        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
    }

    /// <summary>
    /// An updated file is exported with the content of its .txnew.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file in the directory is updated.</para>
    /// <para>When: the directory is exported with ExportAsync.</para>
    /// <para>Then: outside has the new content, the real file on disk has the old content, and the pending change stays Update.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_UpdatedFileHasStagedContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("src/a.txt", content);

        await tx.ExportAsync("src", System.IO.Path.Combine(outside.Path, "dest"));

        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(outside.Path, "dest", "a.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// An uncommitted Add is not included in a directory Export.
    /// </summary>
    /// <remarks>
    /// <para>Given: a real file, and an uncommitted Add in the same directory.</para>
    /// <para>When: the directory is exported with ExportAsync.</para>
    /// <para>Then: only the real file goes outside, and the Add stays pending.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_ExcludesUncommittedAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("src/b.txt", content);

        await tx.ExportAsync("src", System.IO.Path.Combine(outside.Path, "dest"));

        string destination = System.IO.Path.Combine(outside.Path, "dest");
        Assert.Equal("real", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "b.txt")));
        Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A directory Export that succeeded stays after Dispose.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a file.</para>
    /// <para>When: ExportAsync, then Dispose.</para>
    /// <para>Then: the destination outside remains.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_SucceededDirectoryStaysAfterDispose()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportAsync("src", destination);
        }

        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// Canceling a directory Export deletes what was partly created.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a file.</para>
    /// <para>When: the ExportAsync is canceled from a progress report.</para>
    /// <para>Then: OperationCanceledException, and the destination outside does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_CancelDeletesCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.ExportAsync("src", destination, progress, cancellation.Token));

        Assert.False(Directory.Exists(destination));
        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// Junctions under it are not exported.
    /// </summary>
    /// <remarks>
    /// <para>Given: a real file, and a junction to another directory.</para>
    /// <para>When: the directory is exported with ExportAsync.</para>
    /// <para>Then: outside has only the real file, and nothing from the junction target.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task ExportAsync_DoesNotFollowJunctions()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
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
            string destination = System.IO.Path.Combine(outside.Path, "dest");
            await tx.ExportAsync("src", destination);

            Assert.Equal("real", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.False(File.Exists(System.IO.Path.Combine(destination, "secret.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// Exporting an empty directory reports 0 bytes once.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory.</para>
    /// <para>When: ExportAsync runs with a progress receiver.</para>
    /// <para>Then: one report of 0 bytes with a null TotalBytes, and the directory exists outside.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_EmptyDirectoryReportsZeroBytesOnce()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.ExportAsync("src", destination, progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(tx.GetPendingChanges());
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
