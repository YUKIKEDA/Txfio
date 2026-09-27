using System.Text;
using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Text;

public sealed class TextExtensionTests
{
    /// <summary>
    /// WriteAllText to a missing file is an Add, and nothing is on disk until commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist in the work folder.</para>
    /// <para>When: WriteAllTextAsync, then a read.</para>
    /// <para>Then: the pending change is an Add, the content can be read, and the real file does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_MissingFileBecomesAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// WriteAllText to an existing file is an Update, and the disk stays old.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt contains old.</para>
    /// <para>When: WriteAllTextAsync, then commit.</para>
    /// <para>Then: the disk is old before commit and new after success, and the pending change is an Update.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_ExistingFileBecomesUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "new");

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("new", await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Writing again while still new keeps the Add and replaces the content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is not on disk.</para>
    /// <para>When: WriteAllTextAsync is called twice.</para>
    /// <para>Then: one pending Add, and the second content is read.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_RewriteOfNewFileStaysAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "one");
        await tx.WriteAllTextAsync("a.txt", "two");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("two", await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// Writing after a Delete is an Update.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt exists and is deleted.</para>
    /// <para>When: WriteAllTextAsync is called.</para>
    /// <para>Then: one pending Update, and the new content can be read.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_AfterDeleteBecomesUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        await tx.WriteAllTextAsync("a.txt", "new");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Update, pending.Kind);
        Assert.Equal("new", await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// A null string is added as an empty string.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: WriteAllTextAsync is called with null contents.</para>
    /// <para>Then: an empty string is read, and the pending change is an Add.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_WritesNullAsEmptyString()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", null);

        Assert.Equal(string.Empty, await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A write without an encoding is UTF-8 without a BOM.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: it is written without an encoding, then committed.</para>
    /// <para>Then: the first byte is h, and there is no BOM.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_DefaultIsUtf8WithoutBom()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello");
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);

        byte[] bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal((byte)'h', bytes[0]);
        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    /// <summary>
    /// A file with a UTF-8 BOM reads only the characters even without an encoding.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is EF BB BF followed by hello.</para>
    /// <para>When: ReadAllTextAsync is called.</para>
    /// <para>Then: hello, and the BOM is not in the string.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_DetectsBom()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] payload = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        await File.WriteAllBytesAsync(System.IO.Path.Combine(work.Path, "a.txt"), payload);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string text = await tx.ReadAllTextAsync("a.txt");

        Assert.Equal("hello", text);
    }

    /// <summary>
    /// A given encoding round-trips, and with a BOM, a read without an encoding gives the same characters.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: it is written in UTF-16, and read both without and with the encoding.</para>
    /// <para>Then: both are hello, and after commit the first bytes are FF FE.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_GivenEncodingRoundTrips()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello", Encoding.Unicode);

        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt", Encoding.Unicode));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        byte[] bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    /// <summary>
    /// Writing lines adds a trailing newline, and reading lines excludes newlines.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: two lines are written with WriteAllLinesAsync, and read both as lines and as a string.</para>
    /// <para>Then: the lines are a and b, and the string has Environment.NewLine after each line.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllLinesAsync_SeparatesLinesWithNewlines()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllLinesAsync("a.txt", new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, await tx.ReadAllLinesAsync("a.txt"));
        Assert.Equal("a" + Environment.NewLine + "b" + Environment.NewLine, await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// Null contents throw ArgumentNullException without writing.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: null is passed as contents.</para>
    /// <para>Then: ArgumentNullException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllLinesAsync_NullThrowsArgumentNullException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tx.WriteAllLinesAsync("a.txt", null!));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A null encoding does not write.
    /// </summary>
    /// <remarks>
    /// <para>Given: a transaction has begun.</para>
    /// <para>When: WriteAllTextAsync is called with a null encoding.</para>
    /// <para>Then: ArgumentNullException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_NullEncodingThrowsArgumentNullException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tx.WriteAllTextAsync("a.txt", "x", null!));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A missing file and a directory throw the same exceptions as ReadAsync.
    /// </summary>
    /// <remarks>
    /// <para>Given: missing.txt does not exist, and dir is a directory.</para>
    /// <para>When: ReadAllTextAsync is called on each.</para>
    /// <para>Then: the missing file throws ExternalConflictException, and the directory throws UnsupportedOperationException.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_CannotReadMissingFileOrDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "dir"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAllTextAsync("missing.txt"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAllTextAsync("dir"));
    }

    /// <summary>
    /// WriteAllText to a Move destination becomes an Add at the destination and a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is moved to b.txt.</para>
    /// <para>When: WriteAllTextAsync is called on b.txt.</para>
    /// <para>Then: the pending changes are an Add of b.txt and a Delete of a.txt, and the content of b.txt can be read. a.txt remains on disk.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_MoveDestinationBecomesAddAndDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await tx.WriteAllTextAsync("b.txt", "y");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.EndsWith("b.txt", pending[0].Path, StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.EndsWith("a.txt", pending[1].Path, StringComparison.Ordinal);
        Assert.Equal("y", await tx.ReadAllTextAsync("b.txt"));
        Assert.Equal("x", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// WriteAllLines to a Move destination also becomes an Add at the destination and a Delete of the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is moved to b.txt.</para>
    /// <para>When: WriteAllLinesAsync is called on b.txt.</para>
    /// <para>Then: the pending changes are an Add and a Delete, and the written lines can be read.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllLinesAsync_MoveDestinationBecomesAddAndDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await tx.WriteAllLinesAsync("b.txt", new[] { "y" });

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal(new[] { "y" }, await tx.ReadAllLinesAsync("b.txt"));
    }

    /// <summary>
    /// WriteAllText to the destination of a directory Move fails.
    /// </summary>
    /// <remarks>
    /// <para>Given: sub is moved to other.</para>
    /// <para>When: WriteAllTextAsync is called on other.</para>
    /// <para>Then: InvalidOperationException, and the pending change stays the Move.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_DirectoryMoveDestinationThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("other", "y"));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
    }

    /// <summary>
    /// WriteAllText to the source of a Move is an Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is moved to a.bak.</para>
    /// <para>When: WriteAllTextAsync is called on a.txt.</para>
    /// <para>Then: the pending changes are a Move and an Add, a.txt reads the new content, and a.bak reads the old content.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_MoveSourceBecomesAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");

        await tx.WriteAllTextAsync("a.txt", "new");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Add, pending[1].Kind);
        Assert.Equal("new", await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal("old", await tx.ReadAllTextAsync("a.bak"));
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Writing a string to an existing directory fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists at the target path.</para>
    /// <para>When: WriteAllTextAsync is called.</para>
    /// <para>Then: ExternalConflictException, and there is no .txnew and no operation.</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_ExistingDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.WriteAllTextAsync("d", "text"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// Appending to an existing file is an Update with the appended content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt contains "one".</para>
    /// <para>When: "two" is appended twice, then committed.</para>
    /// <para>Then: one Update, and after commit a.txt is "onetwotwo".</para>
    /// </remarks>
    [Fact]
    public async Task AppendAllTextAsync_AppendsToExistingFileAsUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "one");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AppendAllTextAsync("a.txt", "two");
        await tx.AppendAllTextAsync("a.txt", "two");

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("onetwotwo", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// Appending to a missing file is an Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: lines are appended, then committed.</para>
    /// <para>Then: one Add, and after commit the lines of a.txt are x and y.</para>
    /// </remarks>
    [Fact]
    public async Task AppendAllLinesAsync_MissingFileBecomesAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AppendAllLinesAsync("a.txt", new[] { "x", "y" });

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(new[] { "x", "y" }, await File.ReadAllLinesAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Appending with an encoding that has a BOM puts the BOM only at the start.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt does not exist.</para>
    /// <para>When: text is appended twice with UTF-8 (with BOM), then committed.</para>
    /// <para>Then: the file has only one BOM at the start, and the content of both appends.</para>
    /// </remarks>
    [Fact]
    public async Task AppendAllTextAsync_AddsBomOnlyForNewFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.AppendAllTextAsync("a.txt", "ab", Encoding.UTF8);
        await tx.AppendAllTextAsync("a.txt", "cd", Encoding.UTF8);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);

        byte[] bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(work.Path, "a.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c', (byte)'d' }, bytes);
    }

    /// <summary>
    /// Appending to an existing directory fails immediately.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty directory exists at the target path.</para>
    /// <para>When: AppendAllTextAsync is called.</para>
    /// <para>Then: ExternalConflictException, and there is no .txnew and no operation.</para>
    /// </remarks>
    [Fact]
    public async Task AppendAllTextAsync_ExistingDirectoryThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AppendAllTextAsync("d", "text"));

        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
    }
}
