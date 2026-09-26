using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverUnreadableJournalTests
{
    /// <summary>
    /// 壊れたジャーナルは残し、その guid の .txnew だけ消す
    /// </summary>
    /// <remarks>
    /// <para>前提: d/ があり、その中の Add の .txnew と、別 guid の .txnew がある。ジャーナルは途中までの JSON</para>
    /// <para>手順: RecoverAsync してから BeginAsync する</para>
    /// <para>期待: JournalUnreadable。ジャーナルと d/ と別 guid の .txnew は残る。Add の .txnew は消える。Begin は RecoveryRequiredException</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_壊れたジャーナルは残しtxnewだけ消してJournalUnreadableになること()
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
    /// 読めるジャーナルを処理しても、壊れたものがあれば JournalUnreadable を返す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Add と、途中までの JSON のジャーナルがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: JournalUnreadable。Committing の対象は確定し、そのジャーナルは消える。壊れたジャーナルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_壊れたジャーナルがあるとJournalUnreadableを優先すること()
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
    /// 読み取りの共有違反は再送出し、.txnew を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 未コミットの Add 残骸があり、ジャーナルを共有なしで開いている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: IOException。ジャーナルと .txnew は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ジャーナルの読み取りが失敗するとIOExceptionでtxnewを残すこと()
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
    /// 上書きの途中で残った一時ファイルは、読めるジャーナルの復旧で消える
    /// </summary>
    /// <remarks>
    /// <para>前提: 未コミットの Add 残骸と、ジャーナルの隣の .tmp がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack。一時ファイルとジャーナルと .txnew は消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_上書きの一時ファイルを消してからロールバックすること()
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
    /// 操作を積んだあとのジャーナルは完全な JSON で、一時ファイルは残らない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダ</para>
    /// <para>手順: BeginAsync して AddAsync する</para>
    /// <para>期待: tx-*.journal が 1 件あり、中身は JSON として読める。*.tmp は無い</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ジャーナルの上書き後に一時ファイルが残らないこと()
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
    /// 読めないジャーナルの掃除は退避も消し、シンボリックリンクの先は辿らない
    /// </summary>
    /// <remarks>
    /// <para>前提: 壊れたジャーナルがあり、その ID の .txnew と .txnew.prev がワークフォルダにある。ワークフォルダの外を指すディレクトリのシンボリックリンクがあり、外にも同じ ID の .txnew がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: JournalUnreadable で、ワークフォルダの .txnew と .prev は消え、外の .txnew は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_読めないジャーナルの掃除は退避も消しリンクの先は辿らないこと()
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
    /// BeginAsync はジャーナルの一時ファイルを残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダ</para>
    /// <para>手順: BeginAsync する</para>
    /// <para>期待: ジャーナルが 1 つあり、.journal.tmp は無い</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_ジャーナルの一時ファイルを残さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "*.tmp"));
    }

    /// <summary>
    /// 初回のジャーナルを移す前に落ちた一時ファイルは、Recover が消してワークフォルダを塞がない
    /// </summary>
    /// <remarks>
    /// <para>前提: ジャーナルが無く、途中までの JSON の tx-{guid}.journal.tmp だけがある</para>
    /// <para>手順: RecoverAsync してから BeginAsync する</para>
    /// <para>期待: NoPendingTransactions で一時ファイルは消え、BeginAsync は成功する</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ジャーナルの無い一時ファイルは消してNoPendingTransactionsになること()
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
    /// 持ち主が生きている一時ファイルは、ジャーナルがまだ無くても Recover が消さない
    /// </summary>
    /// <remarks>
    /// <para>前提: ジャーナルが無く tx-{guid}.journal.tmp があり、その guid の生存ロックを開いたままにしている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: NoPendingTransactions で、一時ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_持ち主が生きている一時ファイルは消さないこと()
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
}
