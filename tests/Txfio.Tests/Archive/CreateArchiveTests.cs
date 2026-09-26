using System.IO.Compression;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class CreateArchiveTests
{
    /// <summary>
    /// ディレクトリを ZIP にして Add し、コミットで本物のパスに現れる
    /// </summary>
    /// <remarks>
    /// <para>前提: tree に a.txt、sub/b.txt、空の empty がある</para>
    /// <para>手順: tree を out.zip へ CreateArchiveAsync してコミットする</para>
    /// <para>期待: コミット前は Add が 1 件で out.zip は無く、コミット後の ZIP には a.txt、sub/b.txt、empty/ のエントリが元の内容で入っている</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ディレクトリをZIPにしてAddすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(System.IO.Path.Combine(tree, "sub"));
        Directory.CreateDirectory(System.IO.Path.Combine(tree, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "sub", "b.txt"), "beta");
        string archive = System.IO.Path.Combine(work.Path, "out.zip");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("tree", "out.zip");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.False(File.Exists(archive));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Dictionary<string, string> entries = await ReadEntriesAsync(archive);
        Assert.Equal(new[] { "a.txt", "empty/", "sub/b.txt" }, entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("alpha", entries["a.txt"]);
        Assert.Equal("beta", entries["sub/b.txt"]);
    }

    /// <summary>
    /// includeBaseDirectory ではディレクトリ名がエントリのルートになる
    /// </summary>
    /// <remarks>
    /// <para>前提: tree に a.txt があり、空の blank ディレクトリもある</para>
    /// <para>手順: tree と blank を includeBaseDirectory で CreateArchiveAsync してコミットする</para>
    /// <para>期待: tree の ZIP は tree/a.txt、blank の ZIP は blank/ のエントリ 1 つになる</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_includeBaseDirectoryでディレクトリ名を含めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "blank"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("tree", "tree.zip", includeBaseDirectory: true);
        await tx.CreateArchiveAsync("blank", "blank.zip", includeBaseDirectory: true);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Dictionary<string, string> tree = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "tree.zip"));
        Dictionary<string, string> blank = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "blank.zip"));
        Assert.Equal("alpha", Assert.Single(tree, pair => pair.Key == "tree/a.txt").Value);
        Assert.Equal("blank/", Assert.Single(blank).Key);
    }

    /// <summary>
    /// ファイルはファイル名のエントリ 1 つになり、日時と進み具合が付く
    /// </summary>
    /// <remarks>
    /// <para>前提: 内容が hello で、最終更新日時が 2020-05-06 07:08:10 の a.txt がある</para>
    /// <para>手順: includeBaseDirectory を付けて a.zip へ CreateArchiveAsync してコミットする</para>
    /// <para>期待: エントリは a.txt だけで日時は元のファイルと同じ、通知は 5 バイトで全体は null である</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ファイルはファイル名のエントリ1つになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime written = new DateTime(2020, 5, 6, 7, 8, 10, DateTimeKind.Local);
        File.SetLastWriteTime(file, written);
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("a.txt", "a.zip", CompressionLevel.Fastest, includeBaseDirectory: true, progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        using ZipArchive zip = ZipFile.OpenRead(System.IO.Path.Combine(work.Path, "a.zip"));
        ZipArchiveEntry entry = Assert.Single(zip.Entries);
        Assert.Equal("a.txt", entry.FullName);
        Assert.Equal(written, entry.LastWriteTime.DateTime);
        Assert.Equal(new TransferProgress(5, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// 空のディレクトリは空の ZIP になり、最後に 0 バイトを 1 回通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の blank ディレクトリがある</para>
    /// <para>手順: blank を CreateArchiveAsync してコミットする</para>
    /// <para>期待: ZIP にエントリは無く、通知は 0 バイトの 1 回である</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_空のディレクトリは空のZIPになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "blank"));
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync("blank", "blank.zip", progress: progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Empty(await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "blank.zip")));
        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// 出力先と入力の関係が不正なら拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: tree に a.txt があり、ワークフォルダに exists.zip がある</para>
    /// <para>手順: tree の配下、既存の ZIP、親の無いパス、入力と同じパスへ CreateArchiveAsync する。無い入力も試す</para>
    /// <para>期待: 配下と同じパスは InvalidOperationException、それ以外は ExternalConflictException で、未確定操作は無い</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_出力先が不正なら拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        string existing = System.IO.Path.Combine(work.Path, "exists.zip");
        await File.WriteAllTextAsync(existing, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync("tree", System.IO.Path.Combine("tree", "in.zip")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("tree", "tree"));
        ExternalConflictException exists = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateArchiveAsync("tree", "exists.zip"));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateArchiveAsync("tree", System.IO.Path.Combine("missing", "out.zip")));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.CreateArchiveAsync("none", "out.zip"));

        Assert.Equal(existing, exists.Path);
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 入力の配下や出力先にこのトランザクションの操作があれば拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: tree/new.txt を Add し、out.zip も Add している</para>
    /// <para>手順: tree を別の ZIP へ、別のディレクトリを out.zip へ CreateArchiveAsync する</para>
    /// <para>期待: どちらも InvalidOperationException で、未確定操作は Add の 2 件のままである</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_配下や出力先に操作があれば拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "other"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("new"u8.ToArray()))
        {
            await tx.AddAsync(System.IO.Path.Combine("tree", "new.txt"), content);
        }

        await using (MemoryStream content = new MemoryStream("zip"u8.ToArray()))
        {
            await tx.AddAsync("out.zip", content);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("tree", "tree.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateArchiveAsync("other", "out.zip"));

        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 取り消しと破棄では ZIP を残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: tree に a.txt がある</para>
    /// <para>手順: 最初の通知で取り消す CreateArchiveAsync のあと、別の ZIP を作ってコミットせずに Dispose する</para>
    /// <para>期待: 取り消しでは未確定操作が増えず、Dispose のあとはどちらの ZIP も .txnew も残らない</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_取り消しと破棄ではZIPを残さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            CancelOnReport progress = new CancelOnReport(source);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tx.CreateArchiveAsync("tree", "cancelled.zip", progress: progress, cancellationToken: source.Token));

            Assert.Empty(tx.GetPendingChanges());
            await tx.CreateArchiveAsync("tree", "disposed.zip");
        }

        Assert.Empty(Directory.GetFiles(work.Path));
    }

    /// <summary>
    /// 外への ZIP は ReadAsync と同じバイトを入れ、ジャーナルにもロックにも残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: tree/a.txt を Update し、b.txt は Add しただけで本物は無い</para>
    /// <para>手順: tree と b.txt をワークフォルダの外へ ExportArchiveAsync する</para>
    /// <para>期待: ZIP には Update と Add の内容が入り、未確定操作は 2 件のまま、ロックファイルは増えない</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_ステージング済みの内容を外のZIPに入れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync(System.IO.Path.Combine("tree", "a.txt"), content);
        }

        await using (MemoryStream content = new MemoryStream("staged"u8.ToArray()))
        {
            await tx.AddAsync("b.txt", content);
        }

        string lockDirectory = System.IO.Path.Combine(work.Path, ".txfio", "locks");
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;
        string treeZip = System.IO.Path.Combine(outside.Path, "tree.zip");
        string fileZip = System.IO.Path.Combine(outside.Path, "b.zip");

        await tx.ExportArchiveAsync("tree", treeZip);
        await tx.ExportArchiveAsync("b.txt", fileZip);

        Assert.Equal("updated", Assert.Single(await ReadEntriesAsync(treeZip), pair => pair.Key == "a.txt").Value);
        Assert.Equal("staged", Assert.Single(await ReadEntriesAsync(fileZip), pair => pair.Key == "b.txt").Value);
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// 外の ZIP のパスが塞がっている、親が無い、ワークフォルダの中なら拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに a.txt があり、外に exists.zip とディレクトリ sub がある</para>
    /// <para>手順: 既存ファイル、ディレクトリ、親の無いパス、ワークフォルダの中へ ExportArchiveAsync する。無い入力も試す</para>
    /// <para>期待: ワークフォルダの中は ArgumentException、それ以外は ExternalConflictException で、既存ファイルは変わらない</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_ZIPのパスが不正なら拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string existing = System.IO.Path.Combine(outside.Path, "exists.zip");
        await File.WriteAllTextAsync(existing, "keep");
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        string missingParent = System.IO.Path.Combine(outside.Path, "missing", "a.zip");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException file = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", existing));
        ExternalConflictException folder = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", directory));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("a.txt", missingParent));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.ExportArchiveAsync("a.txt", System.IO.Path.Combine(work.Path, "in.zip")));
        await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportArchiveAsync("none.txt", System.IO.Path.Combine(outside.Path, "none.zip")));

        Assert.Equal(existing, file.Path);
        Assert.Equal(directory, folder.Path);
        Assert.Equal(System.IO.Path.GetDirectoryName(missingParent), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(System.IO.Path.Combine(outside.Path, "none.zip")));
    }

    /// <summary>
    /// 成功した外の ZIP は Dispose 後も残り、取り消しでは消える
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに a.txt がある</para>
    /// <para>手順: ExportArchiveAsync して Dispose し、別のトランザクションでは最初の通知で取り消す</para>
    /// <para>期待: 成功した ZIP は残り、取り消した ZIP は残らない</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_成功したZIPは残り取り消しでは消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string kept = System.IO.Path.Combine(outside.Path, "kept.zip");
        string cancelled = System.IO.Path.Combine(outside.Path, "cancelled.zip");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportArchiveAsync("a.txt", kept);
        }

        await using ITransaction again = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => again.ExportArchiveAsync("a.txt", cancelled, progress: progress, cancellationToken: source.Token));

        Assert.Equal("disk", Assert.Single(await ReadEntriesAsync(kept)).Value);
        Assert.NotEmpty(progress.Reports);
        Assert.False(File.Exists(cancelled));
    }

    /// <summary>
    /// ZIP の .txnew は、書く前にジャーナルへ載せる
    /// </summary>
    /// <remarks>
    /// <para>前提: tree/a.txt がある</para>
    /// <para>手順: tree を CreateArchiveAsync し、書き込み中の進捗でジャーナルを読む</para>
    /// <para>期待: 進捗が届いたどの時点でも、ジャーナルに out.zip の .txnew が書いてある</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_txnewより先にジャーナルへ載せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        JournalProbeProgress probe = new JournalProbeProgress(work.Path, "out.zip.");

        await tx.CreateArchiveAsync("tree", "out.zip", progress: probe);

        Assert.True(probe.Reported);
        Assert.True(probe.AlwaysJournaled);
    }

    private static async Task<Dictionary<string, string>> ReadEntriesAsync(string archivePath)
    {
        Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using StreamReader reader = new StreamReader(entry.Open());
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    private sealed class ProgressList : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _source;

        public CancelOnReport(CancellationTokenSource source) => _source = source;

        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value)
        {
            Reports.Add(value);
            _source.Cancel();
        }
    }
}
