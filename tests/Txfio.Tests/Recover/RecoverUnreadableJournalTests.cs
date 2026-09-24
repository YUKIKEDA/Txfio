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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result);
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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, result);
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

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result);
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
}
