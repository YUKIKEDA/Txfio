using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class CommitViewTests
{
    /// <summary>
    /// The content added to the source of a file Move can be read.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a.txt is moved to a.bak, a.txt is added.</para>
    /// <para>When: a.txt and a.bak are read with ReadAsync and checked with ExistsAsync.</para>
    /// <para>Then: a.txt has the new content, a.bak has the old content, and both exist.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ReadsAddAtMoveSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        Assert.Equal("new", await ReadTextAsync(tx, "a.txt"));
        Assert.Equal("old", await ReadTextAsync(tx, "a.bak"));
        Assert.True(await tx.ExistsAsync("a.txt"));
        Assert.True(await tx.ExistsAsync("a.bak"));
    }

    /// <summary>
    /// A path in the middle of a chain reads the bytes after applying from the free end.
    /// </summary>
    /// <remarks>
    /// <para>Given: log.txt and log.1 exist, and log.2 does not.</para>
    /// <para>When: Move(log.1→log.2), Move(log→log.1), Add to log.txt, and the three are read.</para>
    /// <para>Then: log.2 is the old log.1, log.1 is the old log, and log.txt has the new content.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ChainReadsBytesFromFreeEnd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("fresh");
        await tx.AddAsync("log.txt", content);

        Assert.Equal("older", await ReadTextAsync(tx, "log.2"));
        Assert.Equal("current", await ReadTextAsync(tx, "log.1"));
        Assert.Equal("fresh", await ReadTextAsync(tx, "log.txt"));
        Assert.False(await tx.ExistsAsync("missing.txt"));
    }

    /// <summary>
    /// Nothing exists under a DeleteTree.
    /// </summary>
    /// <remarks>
    /// <para>Given: dir/a.txt exists, and dir is scheduled with DeleteTree.</para>
    /// <para>When: dir and dir/a.txt are checked with ExistsAsync, and dir/a.txt is read with ReadAsync.</para>
    /// <para>Then: neither exists, the read throws ExternalConflictException, and the file on disk remains.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_NothingUnderDeleteTree()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "dir");
        string child = System.IO.Path.Combine(dir, "a.txt");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(child, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("dir");

        Assert.False(await tx.ExistsAsync("dir"));
        Assert.False(await tx.ExistsAsync("dir/a.txt"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("dir/a.txt"));
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// Under the destination of a directory Move, the source's files are read, and nothing exists under the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: old/a.txt and mid/b.txt exist, and Move(mid→next) is followed by Move(old→mid).</para>
    /// <para>When: each path is read with ReadAsync or checked with ExistsAsync.</para>
    /// <para>Then: next/b.txt has the mid content, mid/a.txt has the old content, old/a.txt does not exist, and the destination directories mid and next are true.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_UnderDirectoryMoveMatchesSource()
    {
        await using TempDirectory work = TempDirectory.Create();
        string oldDir = System.IO.Path.Combine(work.Path, "old");
        string midDir = System.IO.Path.Combine(work.Path, "mid");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(midDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldDir, "a.txt"), "from-old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(midDir, "b.txt"), "from-mid");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        Assert.Equal("from-mid", await ReadTextAsync(tx, "next/b.txt"));
        Assert.Equal("from-old", await ReadTextAsync(tx, "mid/a.txt"));
        Assert.False(await tx.ExistsAsync("old/a.txt"));
        Assert.False(await tx.ExistsAsync("old"));
        Assert.True(await tx.ExistsAsync("mid"));
        Assert.True(await tx.ExistsAsync("next"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("next"));
    }

    /// <summary>
    /// ExistsAsync of a missing path is false, and of a directory is true.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub exists, and missing.txt does not.</para>
    /// <para>When: ExistsAsync is called.</para>
    /// <para>Then: sub is true, missing.txt is false, and ReadAsync of sub throws UnsupportedOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task ExistsAsync_DirectoryIsTrueAndMissingPathIsFalse()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        Assert.True(await tx.ExistsAsync("sub"));
        Assert.False(await tx.ExistsAsync("missing.txt"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("sub"));
    }

    /// <summary>
    /// ExistsAsync rejects paths under the metadata folder and outside the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: ExistsAsync is called on a path under .txfio and on an absolute path in another folder.</para>
    /// <para>Then: the first throws InvalidOperationException, and the second throws ArgumentException.</para>
    /// </remarks>
    [Fact]
    public async Task ExistsAsync_RejectsMetadataFolderAndOutsideWorkFolder()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExistsAsync(".txfio/foo"));
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ExistsAsync(outside.Path));
    }

    /// <summary>
    /// The view of a staging file has the target path of the operation that wrote its content.
    /// </summary>
    /// <remarks>
    /// <para>Given: dir/a.txt is updated, and there is a staging file whose name does not follow the rule.</para>
    /// <para>When: Resolve is called on the real file b.txt and on dir/a.txt.</para>
    /// <para>Then: b.txt has a null StagedFor, and dir/a.txt has StagedFor dir/a.txt with ContentPath set to that staging file.</para>
    /// </remarks>
    [Fact]
    public async Task Resolve_StagingFileViewHasOperationTargetPath()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "dir", "a.txt");
        string staging = System.IO.Path.Combine(work.Path, "staged-elsewhere.bin");
        string real = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(real, "b");
        JournalOperation[] operations = new[] { new JournalOperation(PendingChangeKind.Update, target, staging) };

        CommitAppearance staged = CommitView.Resolve(operations, target);
        CommitAppearance plain = CommitView.Resolve(operations, real);

        Assert.Equal(target, staged.StagedFor);
        Assert.Equal(staging, staged.ContentPath);
        Assert.Null(plain.StagedFor);
        Assert.Equal(real, plain.ContentPath);
    }

    private static async Task<string> ReadTextAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        using StreamReader reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
