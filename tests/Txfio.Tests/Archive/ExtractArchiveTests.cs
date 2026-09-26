using System.IO.Compression;
using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Archive;

public sealed class ExtractArchiveTests
{
    public static TheoryData<string[]> DangerousNames => new TheoryData<string[]>
    {
        new[] { "../evil.txt" },
        new[] { "a/../../evil.txt" },
        new[] { "/abs.txt" },
        new[] { "C:/drive.txt" },
        new[] { "a//b.txt" },
        new[] { "./a.txt" },
        new[] { "con.txt" },
        new[] { "a<b.txt" },
        new[] { "trail." },
        new[] { "x.txnew" },
        new[] { "a.txt", "A.TXT" },
        new[] { "a", "a/b.txt" },
        new[] { "d/", "d" },
    };

    /// <summary>
    /// 入れ子と空ディレクトリを展開し、各ファイルを Add する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt、sub/b.txt、空の empty/ を持つ ZIP がワークフォルダにある</para>
    /// <para>手順: out へ ExtractArchiveAsync してコミットする</para>
    /// <para>期待: コミット前は Add が 2 件で本物のファイルは無く、コミット後は元の内容で現れ、empty も作られている</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_入れ子と空ディレクトリを展開してAddすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(
            System.IO.Path.Combine(work.Path, "in.zip"),
            ("a.txt", "alpha"),
            ("sub/b.txt", "beta"),
            ("empty/", null));
        string output = System.IO.Path.Combine(work.Path, "out");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExtractArchiveAsync("in.zip", "out");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, change => Assert.Equal(PendingChangeKind.Add, change.Kind));
        Assert.False(File.Exists(System.IO.Path.Combine(output, "a.txt")));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("alpha", await File.ReadAllTextAsync(System.IO.Path.Combine(output, "a.txt")));
        Assert.Equal("beta", await File.ReadAllTextAsync(System.IO.Path.Combine(output, "sub", "b.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(output, "empty")));
    }

    /// <summary>
    /// 展開したファイルはエントリの日時になり、進み具合は合計サイズ付きで通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 日時が 2021-02-03 04:05:06 で内容が hello と world! の 2 エントリを持つ ZIP がある</para>
    /// <para>手順: 進み具合を受け取りながら ExtractArchiveAsync してコミットする</para>
    /// <para>期待: ファイルの日時はエントリと同じで、通知の全体は 11 バイト、最後の通知は 11 バイトである</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_エントリの日時と合計サイズの進み具合になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        DateTime written = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Local);
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), written, ("a.txt", "hello"), ("b.txt", "world!"));
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExtractArchiveAsync("in.zip", "out", progress: progress);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal(written, File.GetLastWriteTime(System.IO.Path.Combine(work.Path, "out", "a.txt")));
        Assert.All(progress.Reports, report => Assert.Equal(11, report.TotalBytes));
        Assert.Equal(11, progress.Reports[^1].BytesCopied);
    }

    /// <summary>
    /// 同じトランザクションで作った未コミットの ZIP を展開できる
    /// </summary>
    /// <remarks>
    /// <para>前提: tree/a.txt から CreateArchiveAsync で tree.zip を作り、まだコミットしていない</para>
    /// <para>手順: tree.zip を copy へ ExtractArchiveAsync してコミットする</para>
    /// <para>期待: copy/a.txt が元の内容で現れる</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_未コミットのZIPを展開できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "tree", "a.txt"), "alpha");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateArchiveAsync("tree", "tree.zip");

        await tx.ExtractArchiveAsync("tree.zip", "copy");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("alpha", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "copy", "a.txt")));
    }

    /// <summary>
    /// 危険なエントリ名があると何も残さずに失敗する
    /// </summary>
    /// <param name="names">ZIP のエントリ名</param>
    /// <remarks>
    /// <para>前提: 危険な名前を含む ZIP がワークフォルダにある</para>
    /// <para>手順: out へ ExtractArchiveAsync する</para>
    /// <para>期待: InvalidDataException になり、out も .txnew も未確定操作も無い</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(DangerousNames))]
    public async Task ExtractArchiveAsync_危険なエントリ名なら何も残さずInvalidDataExceptionになること(string[] names)
    {
        await using TempDirectory work = TempDirectory.Create();
        string archive = System.IO.Path.Combine(work.Path, "in.zip");
        await CreateZipAsync(archive, names.Select(name => (name, (string?)"x")).ToArray());
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => tx.ExtractArchiveAsync("in.zip", "out"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 外の Shift_JIS 名の ZIP をエンコーディングを指定して取り込む
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダの外に、Shift_JIS で 日本語.txt と書いた ZIP がある</para>
    /// <para>手順: Shift_JIS を指定して ImportArchiveAsync してコミットする</para>
    /// <para>期待: 日本語.txt が元の内容で現れ、外の ZIP は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_ShiftJIS名のZIPをエンコーディング指定で取り込むこと()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string archive = System.IO.Path.Combine(outside.Path, "sjis.zip");
        await using (FileStream stream = File.Create(archive))
        {
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, shiftJis);
            await using StreamWriter writer = new StreamWriter(zip.CreateEntry("日本語.txt").Open());
            await writer.WriteAsync("naiyou");
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportArchiveAsync(archive, "out", shiftJis);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("naiyou", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "out", "日本語.txt")));
        Assert.True(File.Exists(archive));
    }

    /// <summary>
    /// ZIP のパスが不正なら拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに in.zip、外にディレクトリ sub がある</para>
    /// <para>手順: ワークフォルダの中の ZIP、外の無い ZIP、外のディレクトリを ImportArchiveAsync し、無い ZIP を ExtractArchiveAsync する</para>
    /// <para>期待: 順に ArgumentException、ExternalConflictException、UnsupportedOperationException、ExternalConflictException で、out は作られない</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_ZIPのパスが不正なら拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string inside = System.IO.Path.Combine(work.Path, "in.zip");
        await CreateZipAsync(inside, ("a.txt", "alpha"));
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => tx.ImportArchiveAsync(inside, "out"));
        await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportArchiveAsync(System.IO.Path.Combine(outside.Path, "none.zip"), "out"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ImportArchiveAsync(directory, "out"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ExtractArchiveAsync("none.zip", "out"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 展開先が不正なら拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: in.zip と既存の exists ディレクトリがあり、busy/new.txt を Add している</para>
    /// <para>手順: exists、親の無いパス、Add の親である busy へ ExtractArchiveAsync する</para>
    /// <para>期待: 先の 2 つは ExternalConflictException、最後は InvalidOperationException で、未確定操作は Add の 1 件のままである</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_展開先が不正なら拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), ("a.txt", "alpha"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "exists"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "busy"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = new MemoryStream("new"u8.ToArray()))
        {
            await tx.AddAsync(System.IO.Path.Combine("busy", "new.txt"), content);
        }

        ExternalConflictException exists = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExtractArchiveAsync("in.zip", "exists"));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExtractArchiveAsync("in.zip", System.IO.Path.Combine("missing", "out")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExtractArchiveAsync("in.zip", "busy"));

        Assert.Equal(System.IO.Path.Combine(work.Path, "exists"), exists.Path);
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), parent.Path);
        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// 取り消しと破棄では展開先ごと残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: sub/a.txt と b.txt を持つ ZIP がある</para>
    /// <para>手順: 最初の通知で取り消す ExtractArchiveAsync のあと、別の展開先へ展開してコミットせずに Dispose する</para>
    /// <para>期待: 取り消しでは未確定操作が増えず、Dispose のあとはどちらの展開先も .txnew も残らない</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_取り消しと破棄では展開先を残さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(System.IO.Path.Combine(work.Path, "in.zip"), ("sub/a.txt", "alpha"), ("b.txt", "beta"));
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            CancelOnReport progress = new CancelOnReport(source);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tx.ExtractArchiveAsync("in.zip", "cancelled", progress: progress, cancellationToken: source.Token));

            Assert.Empty(tx.GetPendingChanges());
            Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "cancelled")));
            await tx.ExtractArchiveAsync("in.zip", "disposed");
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "disposed")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 展開後のサイズの合計が上限を超える ZIP は、何もステージせずに失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 5 バイトの a.txt と 4 バイトの b.txt を持つ ZIP がワークフォルダにある</para>
    /// <para>手順: 上限 8 バイトで ExtractArchiveAsync し、そのあと上限 9 バイトでもう一度展開する</para>
    /// <para>期待: 1 回目は InvalidDataException で、展開先も .txnew も操作も無い。2 回目は成功し Add が 2 件</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_展開後の合計が上限を超えると何もステージしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await CreateZipAsync(
            System.IO.Path.Combine(work.Path, "in.zip"),
            ("a.txt", "alpha"),
            ("b.txt", "beta"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => tx.ExtractArchiveAsync("in.zip", "out", maxExtractedBytes: 8));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
        Assert.Empty(tx.GetPendingChanges());

        await tx.ExtractArchiveAsync("in.zip", "out", maxExtractedBytes: 9);
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 上限が 0 未満なら ArgumentOutOfRangeException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: ZIP がワークフォルダの外にある</para>
    /// <para>手順: 上限 -1 で ImportArchiveAsync する</para>
    /// <para>期待: ArgumentOutOfRangeException で、操作は無い</para>
    /// </remarks>
    [Fact]
    public async Task ImportArchiveAsync_上限が負ならArgumentOutOfRangeExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string archive = System.IO.Path.Combine(outside.Path, "in.zip");
        await CreateZipAsync(archive, ("a.txt", "alpha"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tx.ImportArchiveAsync(archive, "out", maxExtractedBytes: -1));

        Assert.Empty(tx.GetPendingChanges());
    }

    private static Task CreateZipAsync(string path, params (string Name, string? Content)[] entries)
    {
        return CreateZipAsync(path, null, entries);
    }

    private static async Task CreateZipAsync(string path, DateTime? written, params (string Name, string? Content)[] entries)
    {
        await using FileStream stream = File.Create(path);
        using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string? content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            if (written is not null)
            {
                entry.LastWriteTime = written.Value;
            }

            if (content is null)
            {
                continue;
            }

            await using StreamWriter writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(content);
        }
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

        public void Report(TransferProgress value) => _source.Cancel();
    }
}
