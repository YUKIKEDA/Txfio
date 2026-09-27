using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class AddUpdateTests
{
    /// <summary>
    /// An Add creates a .txnew next to the target, and the target path does not exist yet.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: one pending Add, the .txnew exists, and the target file does not.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_OnlyTxnewExistsBeforeCommit()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
        await tx.AddAsync("a.txt", content);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(target, pending[0].Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dispose without commit deletes the .txnew and does not create the target path.
    /// </summary>
    /// <remarks>
    /// <para>Given: right after an Add.</para>
    /// <para>When: the transaction is disposed without Commit.</para>
    /// <para>Then: neither the target nor the .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DisposeWithoutCommitDeletesTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
            await tx.AddAsync("a.txt", content);
        }

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// An Add to an existing file fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists at the target path.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: ExternalConflictException, Path is the target, and there is no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ExistingFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "existing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AddAsync("a.txt", content));
        Assert.Equal(target, ex.Path);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// An Add to an existing directory fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists at the target path.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: ExternalConflictException, Path is the target, there is no .txnew and no operation, and the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ExistingDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AddAsync("d", content));
        Assert.Equal(target, ex.Path);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
        Assert.True(Directory.Exists(target));
    }

    /// <summary>
    /// An Update of an existing directory fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists at the target path.</para>
    /// <para>When: UpdateAsync is called.</para>
    /// <para>Then: ExternalConflictException, Path is the target, and there is no .txnew and no operation.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_ExistingDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.UpdateAsync("d", content));
        Assert.Equal(target, ex.Path);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// An Update of a missing file fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: no file exists at the target path.</para>
    /// <para>When: UpdateAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the target.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_MissingFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.UpdateAsync("missing.txt", content));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing.txt"), ex.Path);
    }

    /// <summary>
    /// A path whose parent directory does not exist is not created automatically.
    /// </summary>
    /// <remarks>
    /// <para>Given: a subfolder does not exist.</para>
    /// <para>When: AddAsync is called on a path under it.</para>
    /// <para>Then: ExternalConflictException, and Path is the parent directory.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_MissingParentThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AddAsync("sub/a.txt", content));
        Assert.Equal(System.IO.Path.Combine(work.Path, "sub"), ex.Path);
    }

    /// <summary>
    /// A path outside the work folder is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: a path outside the work folder.</para>
    /// <para>When: AddAsync is called with that absolute path.</para>
    /// <para>Then: ArgumentException.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_OutsideWorkFolderThrowsArgumentException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory other = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        string outside = System.IO.Path.Combine(other.Path, "a.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => tx.AddAsync(outside, content));
    }

    /// <summary>
    /// Adding the same path again overwrites the .txnew, and the pending changes stay one.
    /// </summary>
    /// <remarks>
    /// <para>Given: the same path has already been added.</para>
    /// <para>When: AddAsync is called again with different content.</para>
    /// <para>Then: one pending change, the .txnew has the later content, and no .prev remains.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_RestageOfSamePathOverwritesAndStaysOne()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.AddAsync("a.txt", second);

        Assert.Single(tx.GetPendingChanges());
        string[] sidecars = Directory.GetFiles(work.Path, "*.txnew");
        Assert.Single(sidecars);
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
        Assert.Empty(Directory.GetFiles(work.Path, "*.prev"));
    }

    /// <summary>
    /// An Update of an added path keeps the Add and replaces only the content.
    /// </summary>
    /// <remarks>
    /// <para>Given: the same path has been added (the target path does not exist yet).</para>
    /// <para>When: UpdateAsync is called.</para>
    /// <para>Then: the pending kind stays Add, and the .txnew has the new content.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_OverwritesUncommittedAddAndStaysAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.UpdateAsync("a.txt", second);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        string[] sidecars = Directory.GetFiles(work.Path, "*.txnew");
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
    }

    /// <summary>
    /// The caller's Stream is not disposed.
    /// </summary>
    /// <remarks>
    /// <para>Given: a MemoryStream is passed.</para>
    /// <para>When: AddAsync is called.</para>
    /// <para>Then: the Stream can still be read after the call.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DoesNotDisposeCallerStream()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
        await tx.AddAsync("a.txt", content);
        Assert.True(content.CanRead);
        content.Dispose();
    }

    /// <summary>
    /// Even if writing the journal fails during a restage of the same path, the .txnew keeps its original content.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, the journal is locked exclusively.</para>
    /// <para>When: AddAsync is called again on the same path.</para>
    /// <para>Then: IOException, one pending Add, and the .txnew has the earlier content.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_RestageJournalWriteFailureKeepsOriginalTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        string workPath = work.Path;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(workPath))
        {
            await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
            await tx.AddAsync("a.txt", first);
            await using FileStream journalLock = LockJournal(workPath);
            await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");

            IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.AddAsync("a.txt", second));
            Assert.Null(ex.InnerException);

            PendingChange pending = Assert.Single(tx.GetPendingChanges());
            Assert.Equal(PendingChangeKind.Add, pending.Kind);
            string[] sidecars = Directory.GetFiles(workPath, "*.txnew");
            Assert.Single(sidecars);
            Assert.Equal("first", await File.ReadAllTextAsync(sidecars[0]));
            Assert.Empty(Directory.GetFiles(workPath, "*.prev"));
        }

        Assert.Empty(Directory.GetFiles(workPath, "*.txnew"));
        Assert.Empty(Directory.GetFiles(workPath, "*.prev"));
    }

    /// <summary>
    /// If writing a restage fails, the .txnew goes back to its original content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added.</para>
    /// <para>When: the same path is restaged with an Update whose progress throws.</para>
    /// <para>Then: InvalidOperationException, the .txnew keeps the Add content, and no .prev remains.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_RestageWriteFailureRestoresTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.UpdateAsync("a.txt", second, new ThrowingProgress()));

        string sidecar = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("first", await File.ReadAllTextAsync(sidecar));
        Assert.Empty(Directory.GetFiles(work.Path, "*.prev"));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// Canceling a restage puts the .txnew back to its original content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added, and the cancellation token is canceled at the first progress report.</para>
    /// <para>When: the same path is updated with that token.</para>
    /// <para>Then: OperationCanceledException, the .txnew keeps the Add content, and no .prev remains.</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_CancelingRestageRestoresTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        using CancellationTokenSource source = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.UpdateAsync("a.txt", second, new CancelOnReport(source), source.Token));

        string sidecar = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("first", await File.ReadAllTextAsync(sidecar));
        Assert.Empty(Directory.GetFiles(work.Path, "*.prev"));
    }

    /// <summary>
    /// The backup of a large restage is restored as the same file, without a copy.
    /// </summary>
    /// <remarks>
    /// <para>Given: a 1 MiB a.txt is added, and the creation time of its .txnew is shifted.</para>
    /// <para>When: the same path is restaged with an Update whose progress throws.</para>
    /// <para>Then: InvalidOperationException, the .txnew keeps the Add content and creation time, and no .prev remains.</para>
    /// </remarks>
    [WindowsFact("A file keeps its creation time when moved")]
    public async Task UpdateAsync_LargeRestageBackupIsRestoredAsSameFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] original = new byte[1024 * 1024];
        original.AsSpan().Fill(0xAB);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = new MemoryStream(original);
        await tx.AddAsync("a.txt", first);
        string sidecar = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        File.SetCreationTimeUtc(sidecar, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        DateTime created = File.GetCreationTimeUtc(sidecar);
        await using MemoryStream second = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.UpdateAsync("a.txt", second, new ThrowingProgress()));

        byte[] actual = await File.ReadAllBytesAsync(sidecar);
        Assert.True(original.AsSpan().SequenceEqual(actual));
        Assert.Equal(created, File.GetCreationTimeUtc(sidecar));
        Assert.Empty(Directory.GetFiles(work.Path, "*.prev"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    private sealed class ThrowingProgress : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => throw new InvalidOperationException("report");
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _source;

        public CancelOnReport(CancellationTokenSource source) => _source = source;

        public void Report(TransferProgress value) => _source.Cancel();
    }
}
