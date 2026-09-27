using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class ReadTests
{
    /// <summary>
    /// The Add content can be read from .txnew.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is added. The real file does not exist.</para>
    /// <para>When: ReadAsync is called.</para>
    /// <para>Then: the Add content is read from position 0, and there is still one lock file.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ReadsAddContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        string lockDirectory = System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, target))!;
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;

        await using Stream stream = await tx.ReadAsync("a.txt");
        Assert.Equal(0, stream.Position);
        Assert.Equal("staged", await ReadTextAsync(stream));
        Assert.False(File.Exists(target));
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// An Update reads the new content, and the old content on disk remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: ReadAsync is called.</para>
    /// <para>Then: the read returns the Update content, and the real file keeps the content before the update.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ReadsUpdateContent()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        Assert.Equal("new", await ReadTextAsync(tx, "a.txt"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// An unstaged file reads the real file.
    /// </summary>
    /// <remarks>
    /// <para>Given: the transaction has staged no file.</para>
    /// <para>When: an existing file is read with ReadAsync.</para>
    /// <para>Then: the real content is read, and there is no lock folder.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ReadsRealFileWhenUnstaged()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        Assert.Equal("disk", await ReadTextAsync(tx, "a.txt"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
    }

    /// <summary>
    /// A file scheduled for Delete does not exist.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is scheduled for Delete.</para>
    /// <para>When: ReadAsync is called.</para>
    /// <para>Then: ExternalConflictException, and the file on disk remains.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ScheduledDeleteThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("a.txt"));
        Assert.Equal(target, missing.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// The Move destination reads the source's bytes, and the source does not exist.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is scheduled to move to b.txt.</para>
    /// <para>When: the source and destination are read with ReadAsync.</para>
    /// <para>Then: the destination reads the source's content, and the source throws ExternalConflictException with Path set to the absolute path of a.txt.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_MoveDestinationReadsSourceBytesAndSourceThrows()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        Assert.Equal("src", await ReadTextAsync(tx, "b.txt"));
        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("a.txt"));
        Assert.Equal(source, missing.Path);
        Assert.Equal("src", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// A path without a file throws ExternalConflictException.
    /// </summary>
    /// <remarks>
    /// <para>Given: the target file does not exist.</para>
    /// <para>When: ReadAsync is called.</para>
    /// <para>Then: ExternalConflictException, and Path is the absolute path of the target.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_MissingFileThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "missing.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("missing.txt"));
        Assert.Equal(target, missing.Path);
    }

    /// <summary>
    /// A directory cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory exists.</para>
    /// <para>When: the directory is read with ReadAsync.</para>
    /// <para>Then: UnsupportedOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_DirectoryThrowsUnsupportedOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("sub"));
    }

    /// <summary>
    /// Paths under the metadata folder and outside the work folder are rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: ReadAsync is called on a path under .txfio and on an absolute path in another folder.</para>
    /// <para>Then: the first throws InvalidOperationException, and the second throws ArgumentException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_RejectsMetadataFolderAndOutsideWorkFolder()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory other = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ReadAsync(".txfio/foo"));
        string outside = System.IO.Path.Combine(other.Path, "a.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ReadAsync(outside));
    }

    /// <summary>
    /// A committed transaction cannot read.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty transaction has been committed.</para>
    /// <para>When: ReadAsync is called.</para>
    /// <para>Then: InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_CommittedTransactionThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ReadAsync("a.txt"));
    }

    /// <summary>
    /// A commit works even while a read is open.
    /// </summary>
    /// <remarks>
    /// <para>Given: the Add content is kept open with ReadAsync.</para>
    /// <para>When: CommitAsync runs, and the open stream is read.</para>
    /// <para>Then: Succeeded, the target file exists, and the stream still reads the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_CommitWorksWhileStreamIsOpen()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await using Stream stream = await tx.ReadAsync("a.txt");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Equal("staged", await ReadTextAsync(stream));
    }

    /// <summary>
    /// Restaging while a read is open fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: the Add content is kept open with ReadAsync.</para>
    /// <para>When: the same path is updated.</para>
    /// <para>Then: IOException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_RestageWhileOpenThrowsIOException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await using Stream stream = await tx.ReadAsync("a.txt");
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("next");

        await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("a.txt", again));
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// If canceled at the start, it does not read.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file.</para>
    /// <para>When: ReadAsync is called with a canceled token.</para>
    /// <para>Then: OperationCanceledException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_CanceledAtStartThrowsOperationCanceledException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tx.ReadAsync("a.txt", source.Token));
    }

    private static async Task<string> ReadTextAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        return await ReadTextAsync(stream);
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
