using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class ProtocolLockTests
{
    private const string SharingModes = "protocol detection relies on Windows file sharing modes";

    /// <summary>
    /// A staging call fails when a client of another protocol version holds its protocol lock.
    /// </summary>
    /// <remarks>
    /// <para>Given: .txfio/proto-2.lock is held open as a holder does.</para>
    /// <para>When: a transaction with a zero wait adds a.txt.</para>
    /// <para>Then: IncompatibleClientException that names protocol 2 and the work folder, nothing is pending, and the work-folder lock file is not created.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_ThrowsWhenAnotherProtocolHoldsItsLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        using FileStream other = HoldAsAnotherVersion(work.Path, 2);
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        IncompatibleClientException incompatible = await Assert.ThrowsAsync<IncompatibleClientException>(
            () => tx.AddAsync("a.txt", content));

        Assert.Contains("other: protocol 2", incompatible.Message, StringComparison.Ordinal);
        Assert.EndsWith(": " + work.Path, incompatible.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(PathLockSet.FilePath(work.Path, work.Path)));
    }

    /// <summary>
    /// RecoverAsync processes nothing while a client of another protocol version is alive.
    /// </summary>
    /// <remarks>
    /// <para>Given: a committing Add journal of a.txt is left, and .txfio/proto-2.lock is held open as a holder does.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: IncompatibleClientException, and the journal and the .txnew remain.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task RecoverAsync_ThrowsWhenAnotherProtocolHoldsItsLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(work.Path, committing: true, "a.txt", "staged");
        using FileStream other = HoldAsAnotherVersion(work.Path, 2);

        await Assert.ThrowsAsync<IncompatibleClientException>(() => TxFileIO.RecoverAsync(work.Path));

        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// A call that waits succeeds when the client of another protocol version goes away within the wait.
    /// </summary>
    /// <remarks>
    /// <para>Given: .txfio/proto-2.lock is held open, and the transaction has a 2-second wait.</para>
    /// <para>When: the handle is closed after 200 ms while a.txt is added.</para>
    /// <para>Then: the Add succeeds.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_SucceedsWhenAnotherProtocolLeavesDuringWait()
    {
        await using TempDirectory work = TempDirectory.Create();
        FileStream other = HoldAsAnotherVersion(work.Path, 2);
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path, TimeSpan.FromSeconds(2));
        Task release = Task.Run(async () =>
        {
            await Task.Delay(200);
            await other.DisposeAsync();
        });
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        await tx.AddAsync("a.txt", content);
        await release;

        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// A lock file that another protocol version left, and nobody holds, does not block.
    /// </summary>
    /// <remarks>
    /// <para>Given: .txfio/proto-2.lock exists, and no handle is open on it.</para>
    /// <para>When: a transaction adds a.txt and commits.</para>
    /// <para>Then: the commit succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_IgnoresLeftoverLockOfAnotherProtocol()
    {
        await using TempDirectory work = TempDirectory.Create();
        HoldAsAnotherVersion(work.Path, 2).Dispose();
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        await tx.AddAsync("a.txt", content);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
    }

    /// <summary>
    /// A probe of a leftover lock file is not taken for a live client.
    /// </summary>
    /// <remarks>
    /// <para>Given: .txfio/proto-2.lock is open in the probe mode (read access, FileShare.Read), as another client checking it would have it.</para>
    /// <para>When: a transaction with a zero wait adds a.txt.</para>
    /// <para>Then: the Add succeeds.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_IgnoresProbeOfLeftoverLock()
    {
        await using TempDirectory work = TempDirectory.Create();
        HoldAsAnotherVersion(work.Path, 2).Dispose();
        using FileStream probe = OpenAsProbe(ProtocolLock.FilePath(work.Path, 2));
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        await tx.AddAsync("a.txt", content);

        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// A probe of its own lock file is waited out even with a zero wait.
    /// </summary>
    /// <remarks>
    /// <para>Given: this version's .txfio/proto-1.lock is open in the probe mode, as another version checking it would have it.</para>
    /// <para>When: the probe is closed after 300 ms while a transaction with a zero wait adds a.txt.</para>
    /// <para>Then: the Add succeeds.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_WaitsOutProbeOfOwnLockWithZeroWait()
    {
        await using TempDirectory work = TempDirectory.Create();
        string own = ProtocolLock.FilePath(work.Path, ProtocolLock.Version);
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        File.WriteAllBytes(own, Array.Empty<byte>());
        FileStream probe = OpenAsProbe(own);
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path, TimeSpan.Zero);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(300);
            await probe.DisposeAsync();
        });
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        await tx.AddAsync("a.txt", content);
        await release;

        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// When its own lock file stays blocked past the grace, the call fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: this version's .txfio/proto-1.lock stays open in the probe mode.</para>
    /// <para>When: a transaction with a zero wait adds a.txt.</para>
    /// <para>Then: IncompatibleClientException after at least the 1-second grace.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_ThrowsAfterGraceWhenOwnLockStaysBlocked()
    {
        await using TempDirectory work = TempDirectory.Create();
        string own = ProtocolLock.FilePath(work.Path, ProtocolLock.Version);
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        File.WriteAllBytes(own, Array.Empty<byte>());
        using FileStream probe = OpenAsProbe(own);
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path, TimeSpan.Zero);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");

        long started = Environment.TickCount64;
        await Assert.ThrowsAsync<IncompatibleClientException>(() => tx.AddAsync("a.txt", content));

        Assert.True(Environment.TickCount64 - started >= 900);
    }

    /// <summary>
    /// A transaction holds its protocol lock from its first locking call until it ends.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: it adds a.txt, then it is disposed.</para>
    /// <para>Then: .txfio/proto-1.lock is held (a probe fails) after the Add, and free (a probe opens) after the dispose.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task AddAsync_HoldsProtocolLockUntilTransactionEnds()
    {
        await using TempDirectory work = TempDirectory.Create();
        string own = ProtocolLock.FilePath(work.Path, ProtocolLock.Version);
        ITransaction tx = await TxFileIO.BeginAsync(work.Path);
        await using (tx)
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");
            await tx.AddAsync("a.txt", content);

            Assert.Throws<IOException>(() => OpenAsProbe(own).Dispose());
        }

        OpenAsProbe(own).Dispose();
    }

    /// <summary>
    /// Begin and reads do not check the protocol, because they take no work-folder lock.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists, and .txfio/proto-2.lock is held open as a holder does.</para>
    /// <para>When: a transaction begins and checks whether a.txt exists.</para>
    /// <para>Then: both succeed, and the result is <see langword="true"/>.</para>
    /// </remarks>
    [WindowsFact(SharingModes)]
    public async Task ExistsAsync_DoesNotCheckTheProtocol()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        using FileStream other = HoldAsAnotherVersion(work.Path, 2);
        await using ITransaction tx = await TxFileIO.BeginAsync(work.Path);

        Assert.True(await tx.ExistsAsync("a.txt"));
    }

    /// <summary>
    /// The lock-file cleanup of RecoverAsync leaves the protocol lock file.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub/ exists, and a transaction has written sub/a.txt and committed.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: the lock file of sub/a.txt is deleted, and .txfio/proto-1.lock remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_KeepsProtocolLockFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using (ITransaction tx = await TxFileIO.BeginAsync(work.Path))
        {
            await tx.WriteAllTextAsync("sub/a.txt", "a");
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        }

        string pathLock = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "sub", "a.txt"));
        Assert.True(File.Exists(pathLock));

        await TxFileIO.RecoverAsync(work.Path);

        Assert.False(File.Exists(pathLock));
        Assert.True(File.Exists(ProtocolLock.FilePath(work.Path, ProtocolLock.Version)));
    }

    private static FileStream HoldAsAnotherVersion(string workFolder, int version)
    {
        Directory.CreateDirectory(MetadataNames.FolderPath(workFolder));
        return new FileStream(
            ProtocolLock.FilePath(workFolder, version),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
    }

    private static FileStream OpenAsProbe(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
