using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class CopyTests
{
    /// <summary>
    /// ファイルのコピーはコピー元を残し、コピー先は Add になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、b.txt は無い</para>
    /// <para>手順: a.txt を b.txt へ CopyAsync する</para>
    /// <para>期待: pending は b.txt の Add で、a.txt は残り、b.txt はまだ無い</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ファイルはコピー元を残してAddになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CopyAsync("a.txt", "b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(destination, pending.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    /// <summary>
    /// ディレクトリのコピーはファイルごとの Add と空ディレクトリを作る
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルと空のサブディレクトリがある</para>
    /// <para>手順: そのディレクトリを CopyAsync する</para>
    /// <para>期待: pending はコピー先ファイルの Add 1 件で、空ディレクトリはあり、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ディレクトリはファイルごとのAddと空ディレクトリになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CopyAsync("src", "dest");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), pending.Path);
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// 未コミット Dispose ではコピーが作ったディレクトリを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルと空のサブディレクトリがあるディレクトリをコピーした直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: コピー先は無く、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_未コミットDisposeでは作ったディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CopyAsync("src", "dest");
        }

        Assert.False(Directory.Exists(destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Empty(Directory.GetFiles(source, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// このトランザクションの .txnew はコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 本物のファイルと、このトランザクションの ID が付いた .txnew が同じディレクトリにある</para>
    /// <para>手順: そのディレクトリを CopyAsync する</para>
    /// <para>期待: コピー先に入るのは本物のファイルだけで、.txnew はコピーされない</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_このトランザクションのtxnewはコピーしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("outside");
        await tx.AddAsync("outside.txt", content);
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        string transactionId = System.IO.Path.GetFileName(journal).Substring("tx-".Length).Replace(".journal", string.Empty);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "leftover." + transactionId + ".txnew"), "skip");

        await tx.CopyAsync("src", "dest");

        string destination = System.IO.Path.Combine(work.Path, "dest");
        string copiedName = System.IO.Path.GetFileName(Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)));
        Assert.Contains("a.txt", copiedName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leftover", copiedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 配下のジャンクションは辿らず、そのエントリもコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 本物のファイルと、別ディレクトリへのジャンクションがある</para>
    /// <para>手順: そのディレクトリを CopyAsync する</para>
    /// <para>期待: コピー先には本物のファイルだけで、ジャンクションの先は無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task CopyAsync_ジャンクションは辿らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string other = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await File.WriteAllTextAsync(System.IO.Path.Combine(other, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(source, "link");
        CreateJunction(link, other);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

            await tx.CopyAsync("src", "dest");

            string destination = System.IO.Path.Combine(work.Path, "dest");
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.False(File.Exists(System.IO.Path.Combine(destination, "secret.txt")));
            Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), Assert.Single(tx.GetPendingChanges()).Path);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// コピー元ディレクトリがジャンクションなら中をコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 中にファイルがあるディレクトリへのジャンクションがある</para>
    /// <para>手順: そのジャンクションを CopyAsync する</para>
    /// <para>期待: コピー先は空のディレクトリで、pending は空、ジャンクションの先は残る</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task CopyAsync_コピー元がジャンクションなら中をコピーしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(System.IO.Path.Combine(target, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(work.Path, "link");
        CreateJunction(link, target);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

            await tx.CopyAsync("link", "dest");

            string destination = System.IO.Path.Combine(work.Path, "dest");
            Assert.True(Directory.Exists(destination));
            Assert.Empty(Directory.GetFileSystemEntries(destination));
            Assert.Empty(tx.GetPendingChanges());
            Assert.Equal("secret", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "secret.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// ファイルのシンボリックリンクはコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルへのシンボリックリンクがある</para>
    /// <para>手順: そのリンクを CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、リンク先は残り、コピー先は無い</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ファイルのシンボリックリンクはInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "target.txt");
        string link = System.IO.Path.Combine(work.Path, "link.txt");
        await File.WriteAllTextAsync(target, "secret");
        File.CreateSymbolicLink(link, target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("link.txt", "copy.txt"));

        Assert.Contains("シンボリックリンクはコピーできません", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "copy.txt")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("secret", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// コピー先が既にあると失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: コピー元とコピー先のファイルがある</para>
    /// <para>手順: CopyAsync する</para>
    /// <para>期待: ExternalConflictException になり、pending は空で、両方の内容は残る</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_コピー先があるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(destination, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CopyAsync("a.txt", "b.txt"));

        Assert.Equal(destination, conflict.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("dest", await File.ReadAllTextAsync(destination));
    }

    /// <summary>
    /// 親が無いコピー先は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: コピー元のファイルがあり、親ディレクトリは無い</para>
    /// <para>手順: 無い親の下へ CopyAsync する</para>
    /// <para>期待: ExternalConflictException になり、ディレクトリは作られない</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_親が無いとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string parent = System.IO.Path.Combine(work.Path, "missing");

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CopyAsync("a.txt", "missing/b.txt"));

        Assert.Equal(parent, conflict.Path);
        Assert.False(Directory.Exists(parent));
    }

    /// <summary>
    /// 同じパスへはコピーできない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: a.txt を a.txt へ CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_同じパスはInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("a.txt", "a.txt"));

        Assert.Contains("同じパスへはコピーできません", error.Message, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// ディレクトリを自分自身の配下へはコピーできない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがある</para>
    /// <para>手順: その配下へ CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、配下は作られない</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_自分の配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.CopyAsync("src", "src/nested"));

        Assert.Contains("ディレクトリを自分自身の配下へはコピーできません", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(source, "nested")));
    }

    /// <summary>
    /// 更新予約したファイルはコピーできない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Update している</para>
    /// <para>手順: a.txt を b.txt へ CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は Update のまま、b.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_更新予約したファイルはInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("a.txt", "b.txt"));

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 配下に操作があるディレクトリはコピーできない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリの配下へ Add している</para>
    /// <para>手順: そのディレクトリを CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、コピー先は作られない</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_配下に操作があるとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("src/a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("src", "dest"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 全削除の配下へはコピーできない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを DeleteTree している</para>
    /// <para>手順: その配下から外へ CopyAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は DeleteTree のまま</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_全削除の配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("tree/a.txt", "b.txt"));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 取り消しでは作りかけのコピー先を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルがあるディレクトリがある</para>
    /// <para>手順: CopyAsync 中に進捗の通知で取り消す</para>
    /// <para>期待: OperationCanceledException になり、コピー先は無く、pending は空</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_取り消しでは作ったディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.CopyAsync("src", "dest", progress, cancellation.Token));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// ファイルコピーの進捗はファイル長を総量にする
    /// </summary>
    /// <remarks>
    /// <para>前提: 81921 バイトのファイルがある</para>
    /// <para>手順: 進捗を受け取って CopyAsync する</para>
    /// <para>期待: 81920 バイト時点と末尾が通知され、総量は 81921</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ファイルの進捗はファイル長を総量にすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] bytes = new byte[81921];
        bytes[81920] = 1;
        await File.WriteAllBytesAsync(System.IO.Path.Combine(work.Path, "a.txt"), bytes);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("a.txt", "b.txt", progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(new TransferProgress(81920, 81921), progress.Reports[0]);
        Assert.Equal(new TransferProgress(81921, 81921), progress.Reports[1]);
    }

    /// <summary>
    /// 空ディレクトリの進捗は 0 バイトを 1 回通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のディレクトリがある</para>
    /// <para>手順: 進捗を受け取って CopyAsync する</para>
    /// <para>期待: TotalBytes が null の 0 バイトが 1 回で、コピー先ディレクトリがある</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_空ディレクトリは0バイトを1回通知すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("src", "dest", progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// ディレクトリコピーの進捗は書き終えた合計で、総量は不明
    /// </summary>
    /// <remarks>
    /// <para>前提: 2 バイトのファイルが 2 つある</para>
    /// <para>手順: 進捗を受け取って CopyAsync する</para>
    /// <para>期待: 通知は 2 回で、総量はすべて null、最後は書き終えた合計が 4 バイト</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ディレクトリの進捗は合計バイトで総量は不明なこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "ab");
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "b.txt"), "cd");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.CopyAsync("src", "dest", progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.All(progress.Reports, report => Assert.Null(report.TotalBytes));
        Assert.Equal(4, progress.Reports[1].BytesCopied);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using Process process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class RecordingProgress : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new List<TransferProgress>();

        public void Report(TransferProgress value) => Reports.Add(value);
    }

    private sealed class CancelOnReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnReport(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Report(TransferProgress value)
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }
}
