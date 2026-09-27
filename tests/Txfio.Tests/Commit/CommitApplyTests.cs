using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitApplyTests
{
    /// <summary>
    /// Committing an Add moves the content to the target path, and deletes the .txnew and the journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add in an empty work folder.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, the target has the content, and there is no .txnew and no journal.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddedContentAppearsAtTarget()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("héllo");
        await tx.AddAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Assert.Equal("héllo", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing an Update replaces the existing file.
    /// </summary>
    /// <remarks>
    /// <para>Given: an existing file is updated.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: the content is the new one, and there is no .txnew.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateReplacesExistingFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// If the Add target was created externally before commit, the result is Failed and the target is not touched.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, the target path is created externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the target keeps what was written externally.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenAddTargetCreatedExternally()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "external");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.Equal("external", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// If the Update target is gone before commit, the result is Failed and the .txnew remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the target file is deleted externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, the target does not exist, and the .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenUpdateTargetIsGone()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        File.Delete(target);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Even if the content changes externally after an Update, the commit succeeds with the disk just before Commit as Before.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the target content is changed externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the target has the Update content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SucceedsWhenContentChangesAfterUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await File.WriteAllTextAsync(target, "external");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// If the Add target already exists at the check, the result is Failed and nothing on disk is touched.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add, the target path is made a directory.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, the directory and the .txnew remain, and the journal is not Committing.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FailsWhenAddTargetExists()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Directory.CreateDirectory(target);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.True(Directory.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.Contains("\"committing\":false", await File.ReadAllTextAsync(journal), StringComparison.Ordinal);
    }

    /// <summary>
    /// A commit mixing Add, Update, and Delete applies all of them.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Update of an existing file, a new Add, and a Delete of another file.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, the Update and Add content is there, and the Delete target is gone.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AppliesMixedAddUpdateDelete()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "gone.txt");
        await File.WriteAllTextAsync(updated, "old");
        await File.WriteAllTextAsync(deleted, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream updateContent = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", updateContent);
        await using MemoryStream addContent = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("b.txt", addContent);
        await tx.DeleteAsync("gone.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(updated));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(deleted));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// If the check passes but applying the Update fails, the result is PartialConflict, and the journal and .txnew are deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the target file is kept open with shared read.</para>
    /// <para>When: CommitAsync runs, then another transaction begins.</para>
    /// <para>Then: PartialConflict, neither the journal nor the .txnew remains, and the target keeps the original content. The next transaction can begin.</para>
    /// </remarks>
    [WindowsFact("An open file cannot be renamed")]
    public async Task CommitAsync_ApplyFailureIsPartialConflictAndDeletesJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.UpdateAsync("a.txt", content);
        await using FileStream locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.PartialConflict, result.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }

    /// <summary>
    /// Applying an Update keeps the target file's attributes and creation time.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt with the hidden attribute and a creation time of 2001-01-01.</para>
    /// <para>When: a.txt is updated and CommitAsync runs.</para>
    /// <para>Then: Succeeded, the content is new, the hidden attribute and creation time are unchanged, and there is no .txnew.</para>
    /// </remarks>
    [WindowsFact("ReplaceFileW moves attributes and the creation time")]
    public async Task CommitAsync_UpdateKeepsTargetAttributesAndCreationTime()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        DateTime created = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(target, created);
        File.SetAttributes(target, FileAttributes.Hidden);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.True((File.GetAttributes(target) & FileAttributes.Hidden) != 0);
        Assert.Equal(created, File.GetCreationTimeUtc(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }
}
