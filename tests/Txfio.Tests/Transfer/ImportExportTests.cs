using Txfio.Tests.Support;

namespace Txfio.Tests.Transfer;

public sealed class ImportExportTests
{
    /// <summary>
    /// An external file is imported as an Add, and the source remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: outside the work folder there is a file with the content hello.</para>
    /// <para>When: ImportAsync, then commit.</para>
    /// <para>Then: one pending Add, progress reports 5 bytes, and after commit both the source and the destination contain hello.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_AddsExternalFileAndKeepsSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string external = System.IO.Path.Combine(outside.Path, "src.txt");
        await File.WriteAllTextAsync(external, "hello");
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportAsync(external, "a.txt", progress);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(new TransferProgress(5, 5), Assert.Single(progress.Reports));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("hello", await File.ReadAllTextAsync(external));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// A path inside the work folder cannot be imported.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file inside the work folder.</para>
    /// <para>When: ImportAsync is called on that file.</para>
    /// <para>Then: ArgumentException.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_InsideWorkFolderThrowsArgumentException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string inside = System.IO.Path.Combine(work.Path, "in.txt");
        await File.WriteAllTextAsync(inside, "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => tx.ImportAsync(inside, "a.txt"));
    }

    /// <summary>
    /// An existing destination throws ExternalConflictException.
    /// </summary>
    /// <remarks>
    /// <para>Given: a source outside, and a.txt in the work folder.</para>
    /// <para>When: ImportAsync targets a.txt.</para>
    /// <para>Then: ExternalConflictException, Path is the absolute path of a.txt, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ExistingDestinationThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string external = System.IO.Path.Combine(outside.Path, "src.txt");
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(external, "new");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(external, "a.txt"));

        Assert.Equal(target, conflict.Path);
        Assert.Equal("new", await File.ReadAllTextAsync(external));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// It conflicts when another transaction holds the destination.
    /// </summary>
    /// <remarks>
    /// <para>Given: one transaction has imported a.txt.</para>
    /// <para>When: the other imports to the same destination.</para>
    /// <para>Then: LockContentionException, and Path is the absolute path of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_DestinationHeldByAnotherTransactionThrowsLockContentionException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string firstSource = System.IO.Path.Combine(outside.Path, "one.txt");
        string secondSource = System.IO.Path.Combine(outside.Path, "two.txt");
        await File.WriteAllTextAsync(firstSource, "one");
        await File.WriteAllTextAsync(secondSource, "two");
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ImportAsync(firstSource, "a.txt");
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.ImportAsync(secondSource, "a.txt"));

        Assert.Equal(target, contention.Path);
    }

    /// <summary>
    /// Copies out an unstaged real file and added content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is on disk, and b.txt is only added with no real file.</para>
    /// <para>When: both are exported with ExportAsync.</para>
    /// <para>Then: the first is the real file and the second the Add content; the pending changes stay one Add, and the locks stay one from the Add.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_CopiesRealAndStagedContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = new MemoryStream("staged"u8.ToArray());
        await tx.AddAsync("b.txt", content);
        string stagedTarget = System.IO.Path.Combine(work.Path, "b.txt");
        string lockDirectory = System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, stagedTarget))!;
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;
        string exportedDisk = System.IO.Path.Combine(outside.Path, "disk.txt");
        string exportedStaged = System.IO.Path.Combine(outside.Path, "staged.txt");

        await tx.ExportAsync("a.txt", exportedDisk);
        await tx.ExportAsync("b.txt", exportedStaged);

        Assert.Equal("disk", await File.ReadAllTextAsync(exportedDisk));
        Assert.Equal("staged", await File.ReadAllTextAsync(exportedStaged));
        Assert.False(File.Exists(stagedTarget));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// It fails when the destination is occupied, has no parent, or is inside the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt in the work folder.</para>
    /// <para>When: ExportAsync targets an existing file, a directory, a path without a parent, and a path inside the work folder.</para>
    /// <para>Then: the first three throw ExternalConflictException, and the last throws ArgumentException.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_RejectsInvalidDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string existing = System.IO.Path.Combine(outside.Path, "exists.txt");
        await File.WriteAllTextAsync(existing, "keep");
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        string missingParent = System.IO.Path.Combine(outside.Path, "missing", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException file = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", existing));
        ExternalConflictException folder = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", directory));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", missingParent));
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ExportAsync("a.txt", System.IO.Path.Combine(work.Path, "in.txt")));

        Assert.Equal(existing, file.Path);
        Assert.Equal(directory, folder.Path);
        Assert.Equal(System.IO.Path.GetDirectoryName(missingParent), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    }

    /// <summary>
    /// A copy that succeeded stays after Dispose, and a canceled one is deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt in the work folder.</para>
    /// <para>When: ExportAsync runs and the transaction is disposed; in another transaction the export is canceled at the first report.</para>
    /// <para>Then: the file that succeeded remains, and the canceled destination does not.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_KeepsSucceededFileAndDeletesCanceledOne()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string kept = System.IO.Path.Combine(outside.Path, "kept.txt");
        string cancelled = System.IO.Path.Combine(outside.Path, "cancelled.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportAsync("a.txt", kept);
        }

        await using ITransaction again = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => again.ExportAsync("a.txt", cancelled, progress, source.Token));

        Assert.Equal("disk", await File.ReadAllTextAsync(kept));
        Assert.NotEmpty(progress.Reports);
        Assert.False(File.Exists(cancelled));
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
