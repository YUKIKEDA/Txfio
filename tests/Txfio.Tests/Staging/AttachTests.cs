using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class AttachTests
{
    /// <summary>
    /// Attach はコミット前に対象を動かさない
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ファイルがある</para>
    /// <para>手順: AttachAsync する</para>
    /// <para>期待: pending は Attach 1 件で、対象は残り、.txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_コミット前は対象を動かさないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Attach, pending.Kind);
        Assert.Equal(target, pending.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 未コミット Dispose では対象ファイルが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: 対象が残り、.txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_未コミットDisposeでは対象が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.AttachAsync("a.txt");
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 無いファイルへの Attach はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスにファイルが無い</para>
    /// <para>手順: AttachAsync する</para>
    /// <para>期待: FileNotFoundException になる</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_無いファイルだとFileNotFoundExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => tx.AttachAsync("missing.txt"));
    }

    /// <summary>
    /// ディレクトリへの Attach は未対応として失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスがディレクトリである</para>
    /// <para>手順: AttachAsync する</para>
    /// <para>期待: IOException になる</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_ディレクトリだとIOExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<IOException>(() => tx.AttachAsync("sub"));
    }

    /// <summary>
    /// 親ディレクトリが無いパスは自動作成しない
    /// </summary>
    /// <remarks>
    /// <para>前提: サブフォルダが無い</para>
    /// <para>手順: その配下へ AttachAsync する</para>
    /// <para>期待: DirectoryNotFoundException になる</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_親が無いとDirectoryNotFoundExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => tx.AttachAsync("missing/a.txt"));
    }

    /// <summary>
    /// Attach のあと Update は Update になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Attach している</para>
    /// <para>手順: UpdateAsync する</para>
    /// <para>期待: pending は Update 1 件で、対象は残り、.txnew がある</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_AttachのあとだとUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Update, pending.Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Attach のあと Delete は Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Attach している</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: pending は Delete 1 件で、対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_AttachのあとだとDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await tx.DeleteAsync("a.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Attach のあと Move は Move になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Attach している</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: pending は Move 1 件で、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_AttachのあとだとMoveになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await tx.MoveAsync("a.txt", "b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// 既にステージングしたパスへの Attach はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを Add している</para>
    /// <para>手順: AttachAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は Add のままである</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_既にステージングしているとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AttachAsync("a.txt"));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
    }

    /// <summary>
    /// Move 先への Attach はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: B へ AttachAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は Move のままである</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_Move先だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AttachAsync("b.txt"));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
    }

    /// <summary>
    /// Attach でジャーナル書き込みに失敗しても pending は空のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ファイルがあり、journal を排他ロックしている</para>
    /// <para>手順: AttachAsync する</para>
    /// <para>期待: 例外は IOException で、pending は空で対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_journal書き込みに失敗するとpendingは空のままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using FileStream journalLock = LockJournal(work.Path);

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.AttachAsync("a.txt"));
        Assert.Null(ex.InnerException);

        Assert.Empty(tx.GetPendingChanges());
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
