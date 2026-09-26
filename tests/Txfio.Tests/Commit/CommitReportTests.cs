using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitReportTests
{
    /// <summary>
    /// 成功したコミットは操作一覧を空にする
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダで Add している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で Operations は空</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_成功の操作一覧は空であること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "hello");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Empty(report.Operations);
    }

    /// <summary>
    /// 検証で拒んだ操作はすべて載り、直したあと同じトランザクションでもう一度コミットできる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt を Add したあと、どちらも外部で作られている</para>
    /// <para>手順: CommitAsync し、外部のファイルを消してからもう一度 CommitAsync する</para>
    /// <para>期待: 1 回目は Failed で両方 AlreadyExists、2 回目は Succeeded で対象は Add の内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_検証失敗のあと直して同じトランザクションでコミットできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");
        string first = System.IO.Path.Combine(work.Path, "a.txt");
        string second = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(first, "external-a");
        await File.WriteAllTextAsync(second, "external-b");

        CommitReport failed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, failed.Result);
        Assert.Equal(2, failed.Operations.Count);
        Assert.Equal(PendingChangeKind.Add, failed.Operations[0].Kind);
        Assert.Equal(OperationDisposition.Rejected, failed.Operations[0].Disposition);
        Assert.Equal(OperationFailureReason.AlreadyExists, failed.Operations[0].Reason);
        Assert.EndsWith("a.txt", failed.Operations[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Null(failed.Operations[0].NewPath);
        Assert.Equal(OperationFailureReason.AlreadyExists, failed.Operations[1].Reason);
        Assert.EndsWith("b.txt", failed.Operations[1].Path, StringComparison.OrdinalIgnoreCase);
        File.Delete(first);
        File.Delete(second);

        CommitReport again = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, again.Result);
        Assert.Empty(again.Operations);
        Assert.Equal("a", await File.ReadAllTextAsync(first));
        Assert.Equal("b", await File.ReadAllTextAsync(second));
    }

    /// <summary>
    /// 移動先が既にある Move は移動先パスと AlreadyExists を載せる
    /// </summary>
    /// <remarks>
    /// <para>前提: Move したあと、移動先は外部で作られている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で NewPath は移動先、理由は AlreadyExists</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_移動先が既にあるとNewPathと理由を載せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(dest, "external");

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(PendingChangeKind.Move, operation.Kind);
        Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
        Assert.Equal(OperationFailureReason.AlreadyExists, operation.Reason);
        Assert.Equal(source, operation.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, operation.NewPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ディレクトリがファイルにすり替わると ReplacedByFile になる
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory したあと、そのパスをファイルにしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で理由は ReplacedByFile</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルにすり替わるとReplacedByFileになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "file");

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(PendingChangeKind.CreateDirectory, operation.Kind);
        Assert.Equal(OperationFailureReason.ReplacedByFile, operation.Reason);
    }

    /// <summary>
    /// ファイルがディレクトリにすり替わると ReplacedByFile になる
    /// </summary>
    /// <remarks>
    /// <para>前提: Update、ファイルの Delete、ファイルの Move の対象を、それぞれディレクトリにしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で 3 件とも Rejected、理由は ReplacedByFile</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリにすり替わるとReplacedByFileになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "b.txt");
        string moved = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(updated, "old");
        await File.WriteAllTextAsync(deleted, "old");
        await File.WriteAllTextAsync(moved, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        await tx.DeleteAsync("b.txt");
        await tx.MoveAsync("c.txt", "d.txt");
        File.Delete(updated);
        File.Delete(deleted);
        File.Delete(moved);
        Directory.CreateDirectory(updated);
        Directory.CreateDirectory(deleted);
        Directory.CreateDirectory(moved);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(3, report.Operations.Count);
        Assert.All(
            report.Operations,
            operation =>
            {
                Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
                Assert.Equal(OperationFailureReason.ReplacedByFile, operation.Reason);
            });
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Update);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Delete);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Move);
    }

    /// <summary>
    /// 消したファイルは Missing、直下に予定外の子があるディレクトリは DirectoryPreconditions になる
    /// </summary>
    /// <remarks>
    /// <para>前提: Update の対象を消し、空ディレクトリの Delete の直下へ外部でファイルが足されている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、Update は Missing、Delete は DirectoryPreconditions</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_対象が無いと直下条件は理由が分かれること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        string dir = System.IO.Path.Combine(work.Path, "sub");
        await File.WriteAllTextAsync(file, "old");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        await tx.DeleteAsync("sub");
        File.Delete(file);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "external.txt"), "no");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Contains(
            report.Operations,
            operation => operation.Kind == PendingChangeKind.Update
                && operation.Reason == OperationFailureReason.Missing);
        Assert.Contains(
            report.Operations,
            operation => operation.Kind == PendingChangeKind.Delete
                && operation.Reason == OperationFailureReason.DirectoryPreconditions);
    }

    /// <summary>
    /// Delete の適用が共有違反なら PartialConflict で、同じインスタンスではやり直せない
    /// </summary>
    /// <remarks>
    /// <para>前提: Delete したあと、対象ファイルを共有なしで開いたままにしている</para>
    /// <para>手順: CommitAsync し、同じトランザクションでもう一度 CommitAsync する</para>
    /// <para>期待: PartialConflict で理由は SharingViolation、2 回目は InvalidOperationException</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_共有違反はPartialConflictで同じインスタンスではやり直せないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using FileStream locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.PartialConflict, report.Result);
        Assert.Equal(OperationDisposition.Skipped, operation.Disposition);
        Assert.Equal(OperationFailureReason.SharingViolation, operation.Reason);
        Assert.Equal(PendingChangeKind.Delete, operation.Kind);
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync());
        Assert.Equal("このトランザクションは既にコミット済みです", again.Message);
    }

    /// <summary>
    /// .txnew をファイルとして読めない検証失敗は IoFailure になる
    /// </summary>
    /// <remarks>
    /// <para>前提: Add と Update のあと、両方の .txnew は消されている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed でどちらも Rejected かつ IoFailure、Update の対象は元の内容のまま</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_txnewが読めないとIoFailureになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(updated, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "added");
        await tx.WriteAllTextAsync("b.txt", "updated");
        foreach (string staging in Directory.GetFiles(work.Path, "*.txnew"))
        {
            File.Delete(staging);
        }

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(
            report.Operations,
            operation =>
            {
                Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
                Assert.Equal(OperationFailureReason.IoFailure, operation.Reason);
            });
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Add);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Update);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(updated));
    }

    /// <summary>
    /// 読み取り専用のファイルへの Update は、検証で ReadOnly として拒み、属性を外せばやり直せる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt へ書いたあと、a.txt を読み取り専用にしている</para>
    /// <para>手順: CommitAsync し、属性を外してからもう一度 CommitAsync する</para>
    /// <para>期待: 1 回目は Failed で理由は ReadOnly、扱いは Rejected、a.txt は元の内容。2 回目は Succeeded で新しい内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_読み取り専用へのUpdateはReadOnlyで拒みやり直せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            CommitReport report = await tx.CommitAsync();

            OperationReport operation = Assert.Single(report.Operations);
            Assert.Equal(CommitResult.Failed, report.Result);
            Assert.Equal(OperationFailureReason.ReadOnly, operation.Reason);
            Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
            Assert.Equal("old", await File.ReadAllTextAsync(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        CommitReport retried = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, retried.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 読み取り専用のファイルの Delete は、検証で ReadOnly として拒む
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Delete したあと、a.txt を読み取り専用にしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で理由は ReadOnly、a.txt は残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_読み取り専用のDeleteはReadOnlyで拒むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            CommitReport report = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, report.Result);
            Assert.Equal(OperationFailureReason.ReadOnly, Assert.Single(report.Operations).Reason);
            Assert.True(File.Exists(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// 読み取り専用のファイルでも Move は拒まない
    /// </summary>
    /// <remarks>
    /// <para>前提: 読み取り専用の a.txt がある</para>
    /// <para>手順: Move(a.txt→b.txt) して CommitAsync する</para>
    /// <para>期待: Succeeded で b.txt がある</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_読み取り専用でもMoveは拒まないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await tx.MoveAsync("a.txt", "b.txt");

            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
            Assert.True(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.SetAttributes(dest, FileAttributes.Normal);
            }
        }
    }
}
