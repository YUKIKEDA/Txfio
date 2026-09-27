using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class CreateDirectoryTests
{
    /// <summary>
    /// An empty directory is created when called, and there is one pending change.
    /// </summary>
    /// <remarks>
    /// <para>Given: only the work folder exists.</para>
    /// <para>When: CreateDirectoryAsync is called.</para>
    /// <para>Then: the empty directory exists, and the pending change is one CreateDirectory.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_CreatesEmptyDirectoryWhenCalled()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateDirectoryAsync("drop");

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.CreateDirectory, pending.Kind);
        Assert.Equal(dir, pending.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discard without commit deletes it with the contents written by the plain file API.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, a child file is written with the plain file API.</para>
    /// <para>When: the transaction is discarded without Commit.</para>
    /// <para>Then: neither the directory nor the child file exists.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_DisposeWithoutCommitDeletesWithContents()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        string child = System.IO.Path.Combine(dir, "a.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            await File.WriteAllTextAsync(child, "from-outside");
        }

        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(child));
    }

    /// <summary>
    /// A path that already exists is not created.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory drop exists.</para>
    /// <para>When: CreateDirectoryAsync is called.</para>
    /// <para>Then: ExternalConflictException, and there are no pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_ExistingPathThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "drop"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateDirectoryAsync("drop"));

        Assert.Contains("The path to create already exists", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// A path without a parent is not created.
    /// </summary>
    /// <remarks>
    /// <para>Given: the directory missing does not exist.</para>
    /// <para>When: CreateDirectoryAsync is called on missing/drop.</para>
    /// <para>Then: ExternalConflictException, and no directory exists.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_MissingParentThrowsExternalConflictException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateDirectoryAsync("missing/drop"));

        Assert.Contains("The parent directory does not exist", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "missing")));
    }

    /// <summary>
    /// A nested CreateDirectory works by the same rules as its parent.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory.</para>
    /// <para>When: CreateDirectoryAsync is called on drop/child.</para>
    /// <para>Then: both directories exist, and there are two pending CreateDirectory changes.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_CreatesNestedDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        await tx.CreateDirectoryAsync("drop/child");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop", "child")));
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.All(tx.GetPendingChanges(), change => Assert.Equal(PendingChangeKind.CreateDirectory, change.Kind));
    }

    /// <summary>
    /// Sibling directories can be created in the same transaction.
    /// </summary>
    /// <remarks>
    /// <para>Given: only the work folder exists.</para>
    /// <para>When: CreateDirectoryAsync is called on drop and other.</para>
    /// <para>Then: both directories exist, and there are two pending changes.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_CreatesSiblingsInSequence()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateDirectoryAsync("drop");
        await tx.CreateDirectoryAsync("other");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// An Add under it appears in the pending changes; a file written with the plain file API does not.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory.</para>
    /// <para>When: drop/a.txt is added with AddAsync, and drop/raw.txt is written with the plain file API.</para>
    /// <para>Then: two pending changes, CreateDirectory and Add.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_UnderDirectoryIsVisibleAsScheduled()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("yes");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "raw.txt"), "raw");

        await tx.AddAsync("drop/a.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            tx.GetPendingChanges(),
            change => change.Path.EndsWith("raw.txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Changes to the created directory itself are not folded.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory.</para>
    /// <para>When: Delete, DeleteTree, Move from and to, Update, and another CreateDirectory are called on that path.</para>
    /// <para>Then: each throws InvalidOperationException, and the pending changes stay one.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_ChangesToPathItselfThrowInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        string drop = System.IO.Path.Combine(work.Path, "drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "src.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        InvalidOperationException delete = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.DeleteAsync("drop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("drop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("drop", "other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("src.txt", "drop"));
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync("drop", content));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateDirectoryAsync("drop"));

        Assert.Contains("This path is already staged by another operation", delete.Message, StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.True(Directory.Exists(drop));
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "src.txt")));
    }

    /// <summary>
    /// A directory with no operation under it can be a copy source.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory, and drop/a.txt is written with the plain file API.</para>
    /// <para>When: drop is copied to copy with CopyAsync.</para>
    /// <para>Then: copy/a.txt becomes an Add, and drop remains.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_CanCopyWhenNoOperationUnderDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "raw");

        await tx.CopyAsync("drop", "copy");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith(System.IO.Path.Combine("copy", "a.txt"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// With an operation under it, the directory cannot be a copy source.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory, and drop/a.txt is added.</para>
    /// <para>When: drop is copied to copy with CopyAsync.</para>
    /// <para>Then: InvalidOperationException, and copy does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_OperationUnderDirectoryThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("drop/a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("drop", "copy"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "copy")));
    }

    /// <summary>
    /// An external directory can be imported under the created directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory, and src/a.txt exists outside the work folder.</para>
    /// <para>When: src is imported to drop/in with ImportAsync.</para>
    /// <para>Then: drop/in/a.txt becomes an Add, and the external src remains.</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ImportsDirectoryUnderIt()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        await tx.ImportAsync(source, "drop/in");

        Assert.True(File.Exists(System.IO.Path.Combine(source, "a.txt")));
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith(System.IO.Path.Combine("drop", "in", "a.txt"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Discard also deletes the staging files of Adds under it, and files placed with the plain file API.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file that existed before the transaction is moved into drop, and drop/new.txt is added.</para>
    /// <para>When: the transaction is discarded without Commit.</para>
    /// <para>Then: neither drop, the moved file, nor the added file exists.</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DeletesAddsAndPlainFilesUnderIt()
    {
        await using TempDirectory work = TempDirectory.Create();
        string moved = System.IO.Path.Combine(work.Path, "drop", "keep.txt");
        string added = System.IO.Path.Combine(work.Path, "drop", "new.txt");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "keep.txt"), "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            File.Move(System.IO.Path.Combine(work.Path, "keep.txt"), moved);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.AddAsync("drop/new.txt", content);
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.False(File.Exists(moved));
        Assert.False(File.Exists(added));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "keep.txt")));
    }

    /// <summary>
    /// Adds outside the tree can continue.
    /// </summary>
    /// <remarks>
    /// <para>Given: drop is created with CreateDirectory.</para>
    /// <para>When: a.txt is added with AddAsync.</para>
    /// <para>Then: two pending changes, CreateDirectory and Add.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ContinuesOutsideTree()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("yes");

        await tx.AddAsync("a.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(tx.GetPendingChanges(), change => change.Kind == PendingChangeKind.Add);
    }

    /// <summary>
    /// A file written with the plain file API can be read with ReadAsync.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, a.txt is written with the plain file API.</para>
    /// <para>When: ReadAllTextAsync is called.</para>
    /// <para>Then: the written content is returned, and the pending changes stay one CreateDirectory.</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_ReadsFileUnderDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "outside");

        string text = await tx.ReadAllTextAsync("drop/a.txt");

        Assert.Equal("outside", text);
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A file under it can be exported outside.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, a.txt is written with the plain file API.</para>
    /// <para>When: it is exported outside the work folder with ExportAsync.</para>
    /// <para>Then: the same content exists outside, and the pending changes stay one CreateDirectory.</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_ExportsFileUnderDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "outside");
        string destination = System.IO.Path.Combine(outside.Path, "a.txt");

        await tx.ExportAsync("drop/a.txt", destination);

        Assert.Equal("outside", await File.ReadAllTextAsync(destination));
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// A DeleteTree of an ancestor is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: parent exists, and drop inside it is created with CreateDirectory.</para>
    /// <para>When: DeleteTreeAsync is called on parent.</para>
    /// <para>Then: InvalidOperationException, and drop remains.</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_AncestorThrowsInvalidOperationException()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "parent"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("parent/drop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("parent"));

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "parent", "drop")));
    }
}
