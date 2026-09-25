using Txfio.Tests.Support;

namespace Txfio.Tests.Transfer;

public sealed class ImportExportTests
{
    /// <summary>
    /// 外のファイルを Add として取り込み、コピー元は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダの外に、内容が hello のファイルがある</para>
    /// <para>手順: ImportAsync してコミットする</para>
    /// <para>期待: 未確定操作は Add が 1 件、進み具合の通知は 5 バイト、コミット後もコピー元とコピー先の内容がどちらも hello である</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_外のファイルをAddしコピー元は残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string external = System.IO.Path.Combine(outside.Path, "src.txt");
        await File.WriteAllTextAsync(external, "hello");
        ProgressList progress = new ProgressList();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.ImportAsync(external, "a.txt", progress);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(new TransferProgress(5, 5), Assert.Single(progress.Reports));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("hello", await File.ReadAllTextAsync(external));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// ワークフォルダの中は取り込めない
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダの中にファイルがある</para>
    /// <para>手順: そのファイルを ImportAsync する</para>
    /// <para>期待: ArgumentException になる</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_ワークフォルダの中はArgumentExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string inside = System.IO.Path.Combine(work.Path, "in.txt");
        await File.WriteAllTextAsync(inside, "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => tx.ImportAsync(inside, "a.txt"));
    }

    /// <summary>
    /// コピー先が既にあると ExternalConflictException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 外にコピー元があり、ワークフォルダに a.txt がある</para>
    /// <para>手順: a.txt へ ImportAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は a.txt の絶対パス、コピー元は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_コピー先があるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string external = System.IO.Path.Combine(outside.Path, "src.txt");
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(external, "new");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ImportAsync(external, "a.txt"));

        Assert.Equal(target, conflict.Path);
        Assert.Equal("new", await File.ReadAllTextAsync(external));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 別トランザクションがコピー先を押さえていると競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方のトランザクションが a.txt を Import している</para>
    /// <para>手順: もう一方が同じコピー先を Import する</para>
    /// <para>期待: LockContentionException になり、Path は a.txt の絶対パスである</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_別トランザクションがコピー先を押さえるとLockContentionExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string firstSource = System.IO.Path.Combine(outside.Path, "one.txt");
        string secondSource = System.IO.Path.Combine(outside.Path, "two.txt");
        await File.WriteAllTextAsync(firstSource, "one");
        await File.WriteAllTextAsync(secondSource, "two");
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ImportAsync(firstSource, "a.txt");
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.ImportAsync(secondSource, "a.txt"));

        Assert.Equal(target, contention.Path);
    }

    /// <summary>
    /// ステージしていない本物と、Add した内容を外へコピーする
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt はディスク上にあり、b.txt は Add しただけで本物は無い</para>
    /// <para>手順: 両方を ExportAsync する</para>
    /// <para>期待: 前者は本物、後者は Add の内容になり、未確定操作は Add の 1 件のまま、ロックは Add 由来の 1 つのままである</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_本物とステージング済みの内容をコピーすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = new MemoryStream("staged"u8.ToArray());
        await tx.AddAsync("b.txt", content);
        string stagedTarget = System.IO.Path.Combine(work.Path, "b.txt");
        string lockDirectory = System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, stagedTarget))!;
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;
        string exportedDisk = System.IO.Path.Combine(outside.Path, "disk.txt");
        string exportedStaged = System.IO.Path.Combine(outside.Path, "staged.txt");

        await tx.ExportAsync("a.txt", exportedDisk);
        await tx.ExportAsync("b.txt", exportedStaged);

        Assert.Equal("disk", await File.ReadAllTextAsync(exportedDisk));
        Assert.Equal("staged", await File.ReadAllTextAsync(exportedStaged));
        Assert.False(File.Exists(stagedTarget));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// コピー先が塞がっている、親が無い、ワークフォルダの中なら失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに a.txt がある</para>
    /// <para>手順: 既存ファイル、ディレクトリ、親の無いパス、ワークフォルダの中へ ExportAsync する</para>
    /// <para>期待: 先の 3 つは ExternalConflictException、最後は ArgumentException になる</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_コピー先が不正なら拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string existing = System.IO.Path.Combine(outside.Path, "exists.txt");
        await File.WriteAllTextAsync(existing, "keep");
        string directory = System.IO.Path.Combine(outside.Path, "sub");
        Directory.CreateDirectory(directory);
        string missingParent = System.IO.Path.Combine(outside.Path, "missing", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException file = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", existing));
        ExternalConflictException folder = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", directory));
        ExternalConflictException parent = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.ExportAsync("a.txt", missingParent));
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ExportAsync("a.txt", System.IO.Path.Combine(work.Path, "in.txt")));

        Assert.Equal(existing, file.Path);
        Assert.Equal(directory, folder.Path);
        Assert.Equal(System.IO.Path.GetDirectoryName(missingParent), parent.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    }

    /// <summary>
    /// 成功したコピーは Dispose 後も残り、取り消しでは消える
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに a.txt がある</para>
    /// <para>手順: ExportAsync して Dispose し、別のトランザクションでは最初の通知で取り消す</para>
    /// <para>期待: 成功したファイルは残り、取り消したコピー先は残らない</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_成功したファイルは残り取り消しでは消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        string kept = System.IO.Path.Combine(outside.Path, "kept.txt");
        string cancelled = System.IO.Path.Combine(outside.Path, "cancelled.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.ExportAsync("a.txt", kept);
        }

        await using ITransaction again = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        CancelOnReport progress = new CancelOnReport(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => again.ExportAsync("a.txt", cancelled, progress, source.Token));

        Assert.Equal("disk", await File.ReadAllTextAsync(kept));
        Assert.NotEmpty(progress.Reports);
        Assert.False(File.Exists(cancelled));
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
