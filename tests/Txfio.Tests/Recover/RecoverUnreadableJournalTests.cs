using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverUnreadableJournalTests
{
    /// <summary>
    /// A broken journal is kept, and only the .txnew files of its guid are deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: d/ exists with the .txnew of an Add inside, and a .txnew of another guid. The journal is truncated JSON.</para>
    /// <para>When: RecoverAsync runs, then BeginAsync.</para>
    /// <para>Then: JournalUnreadable. The journal, d/, and the other guid's .txnew remain. The Add's .txnew is deleted. Begin throws RecoveryRequiredException.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_KeepsBrokenJournalDeletesOnlyTxnewAndReturnsJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        string directory = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(directory);
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            @"d\a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.JournalPath, "{\"version\":1,\"transac");
        string other = System.IO.Path.Combine(
            directory,
            "other." + Guid.NewGuid().ToString("D") + ".txnew");
        await File.WriteAllTextAsync(other, "keep");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(other));
        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path));
        Assert.Equal(work.Path, required.Path);
    }

    /// <summary>
    /// Even after processing readable journals, it returns JournalUnreadable if one is broken.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing Add, and a journal of truncated JSON.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable. The Committing target is finished and its journal deleted. The broken journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_BrokenJournalGivesJournalUnreadablePriority()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles committed = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string directory = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(directory);
        LeftoverAddFiles broken = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            @"d\b.txt",
            "left");
        await File.WriteAllTextAsync(broken.JournalPath, "{\"version\":1,\"transac");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(committed.TargetPath));
        Assert.False(File.Exists(committed.JournalPath));
        Assert.True(File.Exists(broken.JournalPath));
        Assert.False(File.Exists(broken.StagingPath));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// A sharing violation while reading is rethrown, and the .txnew is kept.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of an uncommitted Add, and the journal is open without sharing.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: IOException. The journal and the .txnew remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_JournalReadFailureThrowsIOExceptionAndKeepsTxnew()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        await using FileStream hold = new FileStream(
            leftover.JournalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        Assert.True(hold.CanRead);

        await Assert.ThrowsAsync<IOException>(() => global::Txfio.Txfio.RecoverAsync(work.Path));

        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(File.Exists(leftover.StagingPath));
    }

    /// <summary>
    /// A temporary file left during an overwrite is deleted when a readable journal is recovered.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of an uncommitted Add, and a .tmp next to the journal.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack. The temporary file, the journal, and the .txnew are deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesOverwriteTempFileThenRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        string tempPath = leftover.JournalPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, "partial");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
    }

    /// <summary>
    /// After operations are recorded, the journal is complete JSON and no temporary file remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: BeginAsync, then AddAsync.</para>
    /// <para>Then: there is one tx-*.journal, its content reads as JSON, and there is no *.tmp.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_LeavesNoTempFileAfterJournalOverwrite()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
        await transaction.AddAsync("a.txt", content);

        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string journalPath = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "*.tmp"));
        string json = await File.ReadAllTextAsync(journalPath);
        Assert.Contains("\"committing\":false", json, StringComparison.Ordinal);
        Assert.Contains("Add", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cleanup of an unreadable journal deletes backups too, and does not follow symbolic links.
    /// </summary>
    /// <remarks>
    /// <para>Given: a broken journal, and the .txnew and .txnew.prev of its ID in the work folder. A directory symbolic link points outside the work folder, and outside there is also a .txnew of the same ID.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable; the .txnew and .prev in the work folder are deleted, and the .txnew outside remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UnreadableCleanupDeletesBackupsAndDoesNotFollowLinks()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.JournalPath, "{\"version\":1,\"transac");
        string backup = leftover.StagingPath + ".prev";
        await File.WriteAllTextAsync(backup, "old");
        string stagingName = System.IO.Path.GetFileName(leftover.StagingPath);
        string outsideStaging = System.IO.Path.Combine(outside.Path, stagingName);
        await File.WriteAllTextAsync(outsideStaging, "outside");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(work.Path, "link"), outside.Path);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(backup));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideStaging));
    }

    /// <summary>
    /// BeginAsync leaves no journal temporary file.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty work folder.</para>
    /// <para>When: BeginAsync runs.</para>
    /// <para>Then: there is one journal, and no .journal.tmp.</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_LeavesNoJournalTempFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "*.tmp"));
    }

    /// <summary>
    /// A temporary file left by a crash before the first journal was renamed is deleted by Recover, so it does not block the work folder.
    /// </summary>
    /// <remarks>
    /// <para>Given: there is no journal, only a tx-{guid}.journal.tmp of truncated JSON.</para>
    /// <para>When: RecoverAsync runs, then BeginAsync.</para>
    /// <para>Then: NoPendingTransactions, the temporary file is gone, and BeginAsync succeeds.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesTempFileWithoutJournalAndReturnsNoPendingTransactions()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        string temp = System.IO.Path.Combine(
            metadata,
            "tx-" + Guid.NewGuid().ToString("D") + ".journal.tmp");
        await File.WriteAllTextAsync(temp, "{\"version\":1,\"transac");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.NoPendingTransactions, result.Result);
        Assert.Empty(result.Journals);
        Assert.False(File.Exists(temp));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
    }

    /// <summary>
    /// A temporary file whose owner is alive is not deleted by Recover, even without a journal yet.
    /// </summary>
    /// <remarks>
    /// <para>Given: there is no journal, tx-{guid}.journal.tmp exists, and the liveness lock of that guid is kept open.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: NoPendingTransactions, and the temporary file remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_KeepsTempFileWhoseOwnerIsAlive()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        string journal = System.IO.Path.Combine(metadata, "tx-" + Guid.NewGuid().ToString("D") + ".journal");
        string temp = journal + ".tmp";
        await File.WriteAllTextAsync(temp, "{}");
        using FileStream liveness = LivenessLock.Create(MetadataNames.LivenessLockPath(journal));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.NoPendingTransactions, result.Result);
        Assert.True(File.Exists(temp));
    }

    /// <summary>
    /// A journal with a different version is unreadable, and its .txnew files are not touched.
    /// </summary>
    /// <remarks>
    /// <para>Given: the version of an Add journal is rewritten to 2, and its .txnew exists.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, and both the journal and the .txnew remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DifferentVersionTouchesNothingAndReturnsJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// A journal with a different version and an unknown kind does not touch the .txnew either.
    /// </summary>
    /// <remarks>
    /// <para>Given: the version of an Add journal is rewritten to 2 and its kind to NotAKind, and its .txnew exists.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, and both the journal and the .txnew remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DifferentVersionAndUnknownKindTouchesNothing()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Add\"", json, StringComparison.Ordinal);
        string rewritten = json
            .Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)
            .Replace("\"kind\":\"Add\"", "\"kind\":\"NotAKind\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, rewritten);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.True(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// A journal with a numeric kind is unreadable.
    /// </summary>
    /// <remarks>
    /// <para>Given: in a Committing Add journal, the kind is rewritten to the number 99.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, the journal remains, the .txnew is deleted, and a.txt is not created.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_NumericKindMakesJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        Assert.Contains("\"kind\":\"Add\"", json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json.Replace("\"kind\":\"Add\"", "\"kind\":99", StringComparison.Ordinal));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// A journal with an operation whose path is empty is unreadable.
    /// </summary>
    /// <remarks>
    /// <para>Given: in a Committing Add journal, the path is rewritten to an empty string.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: JournalUnreadable, the journal remains, and the .txnew is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_EmptyPathMakesJournalUnreadable()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        string target = System.Text.Json.JsonSerializer.Serialize(System.IO.Path.Combine(work.Path, "a.txt"));
        Assert.Contains("\"path\":" + target, json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json.Replace("\"path\":" + target, "\"path\":\"\"", StringComparison.Ordinal));

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
    }
}
