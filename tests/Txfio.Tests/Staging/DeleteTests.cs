using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DeleteTests
{
    /// <summary>
    /// Delete はコミット前に対象を消さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ファイルがある</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: pending は Delete 1 件で、対象ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_コミット前は対象を消さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        Assert.True(File.Exists(target));
        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Delete, pending[0].Kind);
        Assert.Equal(target, pending[0].Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 未コミット Dispose では対象ファイルが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Delete した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: 対象ファイルが残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_未コミットDisposeでは対象が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteAsync("a.txt");
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 無いファイルへの Delete はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスにファイルが無い</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: FileNotFoundException になる</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_無いファイルだとFileNotFoundExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => tx.DeleteAsync("missing.txt"));
    }

    /// <summary>
    /// ディレクトリへの Delete は未対応として失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスがディレクトリである</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: IOException になる</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_ディレクトリだとIOExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<IOException>(() => tx.DeleteAsync("sub"));
    }

    /// <summary>
    /// Add のあと Delete は打ち消し合い、pending が空になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを Add している</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: pending は空で、.txnew も対象も無い</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Addのあとだと打ち消して空になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        Assert.Empty(tx.GetPendingChanges());
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Update のあと Delete は Delete になり、.txnew を捨てる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: pending は Delete 1 件で、.txnew は無く対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_UpdateのあとだとDeleteになりtxnewが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        Assert.Equal(PendingChangeKind.Delete, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Delete のあと Add は Update になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Delete している</para>
    /// <para>手順: AddAsync する</para>
    /// <para>期待: pending の種類は Update で、対象はまだ残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DeleteのあとだとUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Update, pending[0].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Add のあと Delete でジャーナル書き込みに失敗しても pending と .txnew は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを Add したあと、journal を排他ロックしている</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: 例外になり、pending は Add のままで .txnew も残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Addのあとでjournal書き込みに失敗するとAddのまま残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        await Assert.ThrowsAsync<IOException>(() => tx.DeleteAsync("a.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Update のあと Delete でジャーナル書き込みに失敗しても pending と .txnew は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update したあと、journal を排他ロックしている</para>
    /// <para>手順: DeleteAsync する</para>
    /// <para>期待: 例外になり、pending は Update のままで .txnew も残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Updateのあとでjournal書き込みに失敗するとUpdateのまま残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        await Assert.ThrowsAsync<IOException>(() => tx.DeleteAsync("a.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Update, pending[0].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
