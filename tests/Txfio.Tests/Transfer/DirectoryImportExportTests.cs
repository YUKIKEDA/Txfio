using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Transfer;

public sealed class DirectoryImportExportTests
{
    /// <summary>
    /// 外のディレクトリはファイルごとの Add と空ディレクトリを作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にファイルと空のサブディレクトリがある</para>
    /// <para>手順: ImportAsync してコミットする</para>
    /// <para>期待: Succeeded でコピー先にファイルと空ディレクトリがあり、コピー元も残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ディレクトリはファイルごとのAddになりコピー元は残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "imported");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportAsync(source, "dest");

        string destination = System.IO.Path.Combine(work.Path, "dest");
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), pending.Path);
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("imported", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal("imported", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
    }

    /// <summary>
    /// 未コミット Dispose では取り込みが作ったディレクトリを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 外のディレクトリを Import した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: コピー先は無く、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_未コミットDisposeでは作ったディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ImportAsync(source, "dest");
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// ディレクトリの Import は取り込み先を予約し、無関係なパスは通す
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にディレクトリがあり、ワークフォルダに別のファイルがある</para>
    /// <para>手順: ディレクトリを Import してから、別トランザクションがそのファイルを Delete し、取り込み先の配下を Add する</para>
    /// <para>期待: 別ファイルは Delete でき、配下の Add は LockContentionException で Path は取り込み先である</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ディレクトリは取り込み先だけを予約すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        string dest = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ImportAsync(source, "dest");

        await second.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync("dest/more.txt", content));

        Assert.Equal(dest, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
        Assert.Equal(PendingChangeKind.Delete, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ワークフォルダを含むディレクトリの配下へは取り込めない
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダの親ディレクトリがある</para>
    /// <para>手順: その親をワークフォルダの配下へ ImportAsync する</para>
    /// <para>期待: InvalidOperationException になり、コピー先は作られない</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_コピー先がコピー元の配下ならInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string parent = System.IO.Path.GetDirectoryName(work.Path)!;
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.ImportAsync(parent, "nested"));

        Assert.Contains("ディレクトリを自分自身の配下へはコピーできません", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "nested")));
    }

    /// <summary>
    /// コピー先が既にある、または親が無いと取り込めない
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にディレクトリがあり、ワークフォルダには既存ディレクトリがある</para>
    /// <para>手順: 既存ディレクトリと、親の無いパスへ ImportAsync する</para>
    /// <para>期待: どちらも ExternalConflictException になり、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_コピー先が不正ならExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        string existing = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(existing);
        string parent = System.IO.Path.Combine(work.Path, "missing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException occupied = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(source, "dest"));
        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(source, "missing/dest"));

        Assert.Equal(existing, occupied.Path);
        Assert.Equal(parent, missing.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// 全削除の配下へはディレクトリを取り込めない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを DeleteTree し、外に別のディレクトリがある</para>
    /// <para>手順: 全削除の配下へ ImportAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は DeleteTree のまま</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_全削除の配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ImportAsync(source, "tree/imported"));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "tree", "imported")));
    }

    /// <summary>
    /// 配下のジャンクションは取り込まない
    /// </summary>
    /// <remarks>
    /// <para>前提: 外に、本物のファイルと別ディレクトリへのジャンクションがある</para>
    /// <para>手順: そのディレクトリを ImportAsync する</para>
    /// <para>期待: コピー先には本物のファイルだけで、ジャンクションの先は無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task ImportAsync_ジャンクションは辿らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        string other = System.IO.Path.Combine(outside.Path, "other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await File.WriteAllTextAsync(System.IO.Path.Combine(other, "secret.txt"), "secret");
        string link = System.IO.Path.Combine(source, "link");
        CreateJunction(link, other);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await tx.ImportAsync(source, "dest");

            string destination = System.IO.Path.Combine(work.Path, "dest");
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.Equal(System.IO.Path.Combine(destination, "a.txt"), Assert.Single(tx.GetPendingChanges()).Path);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// ファイルのシンボリックリンクは取り込めない
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にファイルへのシンボリックリンクがある</para>
    /// <para>手順: そのリンクを ImportAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は空</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ファイルのシンボリックリンクはInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string target = System.IO.Path.Combine(outside.Path, "target.txt");
        string link = System.IO.Path.Combine(outside.Path, "link.txt");
        await File.WriteAllTextAsync(target, "secret");
        File.CreateSymbolicLink(link, target);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.ImportAsync(link, "a.txt"));

        Assert.Contains("シンボリックリンクはコピーできません", error.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("secret", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 空ディレクトリの取り込みは 0 バイトを 1 回通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 外に空のディレクトリがある</para>
    /// <para>手順: 進捗を受け取って ImportAsync する</para>
    /// <para>期待: TotalBytes が null の 0 バイトが 1 回で、コピー先ディレクトリがある</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_空ディレクトリは0バイトを1回通知すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.ImportAsync(source, "dest", progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 取り込みの取り消しでは作りかけを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にファイルがあるディレクトリがある</para>
    /// <para>手順: ImportAsync 中に進捗の通知で取り消す</para>
    /// <para>期待: OperationCanceledException になり、コピー先は無く、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_取り消しでは作ったディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.ImportAsync(source, "dest", progress, cancellation.Token));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "dest")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// ディレクトリの Export は外に中身を出し、ワークフォルダは変えない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルと空のサブディレクトリがある</para>
    /// <para>手順: ExportAsync する</para>
    /// <para>期待: 外にファイルと空ディレクトリがあり、ワークフォルダは元のまま、pending は空、ロックは無い</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_ディレクトリは外にコピーしワークフォルダは変えないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ExportAsync("src", destination);

        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
    }

    /// <summary>
    /// Update 済みのファイルは .txnew の内容で出る
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリ内のファイルを Update している</para>
    /// <para>手順: そのディレクトリを ExportAsync する</para>
    /// <para>期待: 外は新しい内容、ディスク上の本物は古い内容、pending は Update のまま</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_Update済みはステージングの内容になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("src/a.txt", content);

        await tx.ExportAsync("src", System.IO.Path.Combine(outside.Path, "dest"));

        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(outside.Path, "dest", "a.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 未コミットの Add はディレクトリの Export に含まれない
    /// </summary>
    /// <remarks>
    /// <para>前提: 本物のファイルと、同じディレクトリへの未コミット Add がある</para>
    /// <para>手順: そのディレクトリを ExportAsync する</para>
    /// <para>期待: 外に出るのは本物のファイルだけで、Add は pending に残る</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_未コミットのAddは含まれないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "real");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("src/b.txt", content);

        await tx.ExportAsync("src", System.IO.Path.Combine(outside.Path, "dest"));

        string destination = System.IO.Path.Combine(outside.Path, "dest");
        Assert.Equal("real", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "b.txt")));
        Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 成功したディレクトリの Export は Dispose 後も残る
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルがあるディレクトリがある</para>
    /// <para>手順: ExportAsync して Dispose する</para>
    /// <para>期待: 外のコピー先が残る</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_成功したディレクトリはDispose後も残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportAsync("src", destination);
        }

        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// ディレクトリ Export の取り消しでは作りかけを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルがあるディレクトリがある</para>
    /// <para>手順: ExportAsync 中に進捗の通知で取り消す</para>
    /// <para>期待: OperationCanceledException になり、外のコピー先は無い</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_取り消しでは作ったディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "disk");
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tx.ExportAsync("src", destination, progress, cancellation.Token));

        Assert.False(Directory.Exists(destination));
        Assert.Equal("disk", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
    }

    /// <summary>
    /// 配下のジャンクションは外へ出さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 本物のファイルと、別ディレクトリへのジャンクションがある</para>
    /// <para>手順: そのディレクトリを ExportAsync する</para>
    /// <para>期待: 外には本物のファイルだけで、ジャンクションの先は無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task ExportAsync_ジャンクションは辿らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
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
            string destination = System.IO.Path.Combine(outside.Path, "dest");
            await tx.ExportAsync("src", destination);

            Assert.Equal("real", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
            Assert.False(Directory.Exists(System.IO.Path.Combine(destination, "link")));
            Assert.False(File.Exists(System.IO.Path.Combine(destination, "secret.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// 空ディレクトリの Export は 0 バイトを 1 回通知する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のディレクトリがある</para>
    /// <para>手順: 進捗を受け取って ExportAsync する</para>
    /// <para>期待: TotalBytes が null の 0 バイトが 1 回で、外にディレクトリがある</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_空ディレクトリは0バイトを1回通知すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));
        string destination = System.IO.Path.Combine(outside.Path, "dest");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        RecordingProgress progress = new RecordingProgress();

        await tx.ExportAsync("src", destination, progress);

        Assert.Equal(new TransferProgress(0, null), Assert.Single(progress.Reports));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(tx.GetPendingChanges());
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
