using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class ExternalChangeTests
{
    /// <summary>
    /// 既定では、ステージ後に中身だけ変わった Update を失敗にしない
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges を渡さず Update したあと、本物の中身が変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で、ファイルはステージした内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_既定では中身だけの変更を失敗にしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Empty(report.Operations);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// サイズが違う Update は ExternalChange で Failed になり、本物を戻してからコミットできる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、Update したあと、本物のサイズが変わっている</para>
    /// <para>手順: CommitAsync し、本物の内容と最終更新日時を戻してもう一度 CommitAsync する</para>
    /// <para>期待: 1 回目は Failed で ExternalChange（本物は外部の内容のまま）、2 回目は Succeeded</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_サイズが違うとExternalChangeで失敗し直してからコミットできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");

        CommitReport failed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, failed.Result);
        OperationReport rejected = Assert.Single(failed.Operations);
        Assert.Equal(PendingChangeKind.Update, rejected.Kind);
        Assert.Equal(OperationDisposition.Rejected, rejected.Disposition);
        Assert.Equal(OperationFailureReason.ExternalChange, rejected.Reason);
        Assert.EndsWith("a.txt", rejected.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, "hello");
        File.SetLastWriteTimeUtc(file, stamp);

        CommitReport again = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, again.Result);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// 最終更新日時だけ違っても ExternalChange になる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、Update したあと、本物の最終更新日時だけが進んでいる</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で ExternalChange、本物の内容は変わらない</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_最終更新日時が違うとExternalChangeになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.Zero, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.Equal("hello", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// 同じサイズで同じ最終更新日時の書き換えは見逃す
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、Update したあと、同じ長さの別内容に書き換え、最終更新日時は元に戻してある</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で、ファイルはステージした内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_同じサイズで同じ最終更新日時は見逃すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "world");
        await File.WriteAllTextAsync(file, "HELLO");
        File.SetLastWriteTimeUtc(file, stamp);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("world", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// 違った Update はすべて Operations に載る
    /// </summary>
    /// <remarks>
    /// <para>前提: 2 ファイルを Update したあと、どちらもサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で両方 ExternalChange、本物は外部の内容のまま</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_違ったUpdateをすべて載せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string first = System.IO.Path.Combine(work.Path, "a.txt");
        string second = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "A");
        await tx.WriteAllTextAsync("b.txt", "B");
        await File.WriteAllTextAsync(first, "aa");
        await File.WriteAllTextAsync(second, "bb");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(report.Operations, operation => Assert.Equal(OperationFailureReason.ExternalChange, operation.Reason));
        Assert.Equal("aa", await File.ReadAllTextAsync(first));
        Assert.Equal("bb", await File.ReadAllTextAsync(second));
    }

    /// <summary>
    /// ファイルが無いときは Missing のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Update したあと、本物が消えている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で Missing</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルが無いときはMissingのままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.Delete(file);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.Missing, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// ディレクトリに変わったときは ReplacedByFile のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Update したあと、本物がディレクトリに変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で ReplacedByFile</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリに変わるとReplacedByFileのままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.Delete(file);
        Directory.CreateDirectory(file);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ReplacedByFile, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// Read の記録があるパスは、そのあと本物が変わってからステージしても記録を更新しない
    /// </summary>
    /// <remarks>
    /// <para>前提: ReadAsync のあと本物のサイズが変わり、そのあと Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で ExternalChange</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Readの記録は再ステージで更新しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await using (Stream stream = await tx.ReadAsync("a.txt"))
        {
        }

        await File.WriteAllTextAsync(file, "external-longer");
        await tx.WriteAllTextAsync("a.txt", "next");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// 実ファイルの Read は記録をその時点へ更新する
    /// </summary>
    /// <remarks>
    /// <para>前提: Read のあと本物のサイズが変わり、もう一度 Read してから Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で、ファイルはステージした内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_実ファイルのReadは記録を更新すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await using (Stream first = await tx.ReadAsync("a.txt"))
        {
        }

        await File.WriteAllTextAsync(file, "external-longer");
        await using (Stream second = await tx.ReadAsync("a.txt"))
        {
        }

        await tx.WriteAllTextAsync("a.txt", "next");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// このトランザクションのステージングファイル（.txnew）を読んでも、記録は更新しない
    /// </summary>
    /// <remarks>
    /// <para>前提: Update のあと本物のサイズが変わり、ReadAsync は .txnew を読んでいる</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で ExternalChange、本物は外部の内容のまま</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_txnewのReadは記録を更新しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");
        await using (Stream stream = await tx.ReadAsync("a.txt"))
        {
            using StreamReader reader = new StreamReader(stream);
            Assert.Equal("next", await reader.ReadToEndAsync());
        }

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// Read していない Update は、再ステージのたびに記録を更新する
    /// </summary>
    /// <remarks>
    /// <para>前提: Update のあと本物のサイズが変わり、もう一度 Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で、ファイルは再ステージした内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Readが無いUpdateは再ステージで記録を更新すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");
        await tx.WriteAllTextAsync("a.txt", "later");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("later", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// Move 先への Update を畳んだあと、元の実ファイルが違えば残った操作を ExternalChange にする
    /// </summary>
    /// <remarks>
    /// <para>前提: Move の先を Update したあと、移動元のサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で Add と Delete が ExternalChange、移動元は外部の内容のまま、移動先は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_畳んだUpdateは元の実ファイルが違うと失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.WriteAllTextAsync("b.txt", "next");
        await File.WriteAllTextAsync(source, "external-longer");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Add && operation.Reason == OperationFailureReason.ExternalChange);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Delete && operation.Reason == OperationFailureReason.ExternalChange);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// ステージ後に書き換えられたファイルの Delete は ExternalChange で Failed になる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、a.txt を Delete したあと、本物のサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed であり、理由は ExternalChange、a.txt は外部の内容のまま残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_書き換えられたファイルのDeleteはExternalChangeで失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.DeleteAsync("a.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(OperationFailureReason.ExternalChange, operation.Reason);
        Assert.Equal(PendingChangeKind.Delete, operation.Kind);
        Assert.Equal("external change", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// ステージ後に書き換えられたファイルの Move は ExternalChange で Failed になる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、Move(a.txt→b.txt) したあと、a.txt のサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed であり、理由は ExternalChange、a.txt は残り、b.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_書き換えられたファイルのMoveはExternalChangeで失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 読んだあとで書き換えられたファイルの Delete も、読んだ時点と比べて Failed になる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、a.txt を読んだあと、本物のサイズが変わってから Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed であり、理由は ExternalChange</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_読んだあとで書き換えられたファイルのDeleteも失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        await File.WriteAllTextAsync(file, "external change");
        await tx.DeleteAsync("a.txt");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// Move のあと移動先を Delete して元の Delete に畳んでも、元のファイルの記録で比べる
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges が true であり、Move(a.txt→b.txt) のあと b.txt を Delete し（元の Delete に畳む）、a.txt のサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed であり、理由は ExternalChange、a.txt は残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_畳んだDeleteも元のファイルの記録で比べること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// 既定では、ステージ後に書き換えられたファイルの Delete を失敗にしない
    /// </summary>
    /// <remarks>
    /// <para>前提: detectExternalChanges を渡さず a.txt を Delete したあと、本物のサイズが変わっている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded であり、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_既定では書き換えられたファイルのDeleteを失敗にしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await File.WriteAllTextAsync(file, "external change");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(File.Exists(file));
    }
}
