using System.IO.Compression;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class CreateArchiveEntryListTests
{
    /// <summary>
    /// 指定した名前で、リストの順に入れる
    /// </summary>
    /// <remarks>
    /// <para>前提: reports/x.csv と a.txt がある</para>
    /// <para>手順: reports/x.csv を m/09.csv、a.txt を名前の省略で組にして CreateArchiveAsync し、コミットする</para>
    /// <para>期待: エントリは m/09.csv、a.txt の順で、内容は元のファイルと同じ、未確定操作は Add の 1 件だった</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_指定した名前でリストの順に入れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "reports"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "reports", "x.csv"), "csv");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[]
            {
                new ArchiveEntrySource(System.IO.Path.Combine("reports", "x.csv"), "m/09.csv"),
                new ArchiveEntrySource("a.txt"),
            },
            "out.zip");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        List<(string Name, string Content)> entries = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "out.zip"));
        Assert.Equal(new[] { ("m/09.csv", "csv"), ("a.txt", "alpha") }, entries);
    }

    /// <summary>
    /// 名前の省略は相対パスになり、ディレクトリの空文字はルートに置く
    /// </summary>
    /// <remarks>
    /// <para>前提: reports/x.csv と、a.txt と空の empty を持つ tree がある</para>
    /// <para>手順: reports/x.csv を省略、tree を省略、tree を空文字、tree を data\sub で組にして CreateArchiveAsync し、コミットする</para>
    /// <para>期待: reports/x.csv、tree/a.txt、tree/empty/、a.txt、empty/、data/sub/a.txt、data/sub/empty/ が入る</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_名前の省略は相対パスで空文字はルートになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "reports"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree", "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "reports", "x.csv"), "csv");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[]
            {
                new ArchiveEntrySource(System.IO.Path.Combine("reports", "x.csv")),
                new ArchiveEntrySource("tree"),
                new ArchiveEntrySource("tree", string.Empty),
                new ArchiveEntrySource("tree", @"data\sub"),
            },
            "out.zip");

        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        List<(string Name, string Content)> entries = await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "out.zip"));
        Assert.Equal(
            new[] { "a.txt", "data/sub/a.txt", "data/sub/empty/", "empty/", "reports/x.csv", "tree/a.txt", "tree/empty/" },
            entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 同じ元を別の名前で 2 回入れられ、空のリストは空の ZIP になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: a.txt を one.txt と two.txt で組にした ZIP と、空のリストの ZIP を作ってコミットする</para>
    /// <para>期待: 前者は同じ内容のエントリが 2 つ、後者はエントリが無く、空の方の通知は 0 バイトの 1 回である</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_同じ元の2回と空のリストを許すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateArchiveAsync(
            new[] { new ArchiveEntrySource("a.txt", "one.txt"), new ArchiveEntrySource("a.txt", "two.txt") },
            "twice.zip");
        await tx.CreateArchiveAsync(Array.Empty<ArchiveEntrySource>(), "empty.zip", progress: progress);

        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        Assert.Equal(
            new[] { ("one.txt", "alpha"), ("two.txt", "alpha") },
            await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "twice.zip")));
        Assert.Empty(await ReadEntriesAsync(System.IO.Path.Combine(work.Path, "empty.zip")));
        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// 渡したエントリ名が不正なら、ロックも ZIP も残さずに ArgumentException になる
    /// </summary>
    /// <param name="entryName">ファイルに付けるエントリ名</param>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: 不正な名前で CreateArchiveAsync する</para>
    /// <para>期待: ArgumentException になり、未確定操作、ZIP、.txnew、ロックファイルのどれも無い</para>
    /// </remarks>
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("con.txt")]
    [InlineData("a:b.txt")]
    [InlineData("x.txnew")]
    [InlineData("")]
    [InlineData("dir/")]
    public async Task CreateArchiveAsync_不正なエントリ名はArgumentExceptionになること(string entryName)
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt", entryName) }, "out.zip"));

        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "out.zip")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        string locks = System.IO.Path.Combine(work.Path, ".txfio", "locks");
        Assert.True(!Directory.Exists(locks) || Directory.GetFiles(locks).Length == 0);
    }

    /// <summary>
    /// ディレクトリを歩いてできた名前がぶつかると、ZIP を残さずに ArgumentException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を持つ tree と、b.txt がある</para>
    /// <para>手順: tree を空文字（ルート）にし、b.txt を A.TXT として同じ ZIP に入れる</para>
    /// <para>期待: ArgumentException になり、未確定操作も ZIP も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_歩いてできた名前の重複はArgumentExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.CreateArchiveAsync(
                new[] { new ArchiveEntrySource("tree", string.Empty), new ArchiveEntrySource("b.txt", "A.TXT") },
                "out.zip"));

        Assert.Empty(tx.GetPendingChanges());
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "out.zip")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// null、ステージ済みの要素、要素の配下への ZIP は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Update し、b.txt と、ディレクトリ tree がある</para>
    /// <para>手順: null の列と null の要素、a.txt を含むリスト、tree の配下への ZIP を試す</para>
    /// <para>期待: 順に ArgumentNullException が 2 つ、InvalidOperationException が 2 つで、未確定操作は Update の 1 件のままである</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_nullとステージ済みと配下へのZIPを拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync("a.txt", content);
        }

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tx.CreateArchiveAsync((IEnumerable<ArchiveEntrySource>)null!, "out.zip"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tx.CreateArchiveAsync(new ArchiveEntrySource[] { null! }, "out.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync(new[] { new ArchiveEntrySource("b.txt"), new ArchiveEntrySource("a.txt") }, "out.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CreateArchiveAsync(
                new[] { new ArchiveEntrySource("b.txt"), new ArchiveEntrySource("tree") },
                System.IO.Path.Combine("tree", "in.zip")));

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 外への ZIP は ReadAsync と同じバイトを指定した名前で入れる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Update し、b.txt は Add しただけで本物は無い</para>
    /// <para>手順: a.txt を x.txt、b.txt を名前の省略で組にしてワークフォルダの外へ ExportArchiveAsync する</para>
    /// <para>期待: ZIP の x.txt は Update の内容、b.txt は Add の内容で、未確定操作は 2 件のままである</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_ステージング済みの内容を指定した名前で入れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("updated"u8.ToArray()))
        {
            await tx.UpdateAsync("a.txt", content);
        }

        await using (MemoryStream content = new MemoryStream("staged"u8.ToArray()))
        {
            await tx.AddAsync("b.txt", content);
        }

        string archive = System.IO.Path.Combine(outside.Path, "out.zip");

        await tx.ExportArchiveAsync(
            new[] { new ArchiveEntrySource("a.txt", "x.txt"), new ArchiveEntrySource("b.txt") },
            archive);

        Assert.Equal(new[] { ("x.txt", "updated"), ("b.txt", "staged") }, await ReadEntriesAsync(archive));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 外への ZIP でも、不正な名前なら ZIP を残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt がある</para>
    /// <para>手順: 両方を same.txt として ExportArchiveAsync する</para>
    /// <para>期待: ArgumentException になり、外に ZIP は無い</para>
    /// </remarks>
    [Fact]
    public async Task ExportArchiveAsync_名前の重複はArgumentExceptionでZIPを残さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "alpha");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "beta");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string archive = System.IO.Path.Combine(outside.Path, "out.zip");

        await Assert.ThrowsAsync<ArgumentException>(
            () => tx.ExportArchiveAsync(
                new[] { new ArchiveEntrySource("a.txt", "same.txt"), new ArchiveEntrySource("b.txt", "same.txt") },
                archive));

        Assert.False(File.Exists(archive));
    }

    private static async Task<List<(string Name, string Content)>> ReadEntriesAsync(string archivePath)
    {
        List<(string Name, string Content)> entries = new List<(string Name, string Content)>();
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using StreamReader reader = new StreamReader(entry.Open());
            entries.Add((entry.FullName, await reader.ReadToEndAsync()));
        }

        return entries;
    }

    private sealed class ProgressList : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }
}
