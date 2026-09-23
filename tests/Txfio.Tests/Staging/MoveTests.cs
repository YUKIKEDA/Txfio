using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class MoveTests
{
    /// <summary>
    /// Move はコミット前に対象を動かさない
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ファイルがある</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: pending は Move 1 件で、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_コミット前は対象を動かさないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 未コミット Dispose では元ファイルが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Move した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: 元が残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_未コミットDisposeでは元が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("a.txt", "b.txt");
        }

        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 無いファイルへの Move はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元にファイルが無い</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は移動元である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_無いファイルだとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("missing.txt", "b.txt"));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing.txt"), ex.Path);
    }

    /// <summary>
    /// 移動先が既にあると失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動先にファイルがある</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は移動先である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先があるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "dst");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "b.txt"));
        Assert.Equal(dest, ex.Path);
    }

    /// <summary>
    /// 移動先がディレクトリだと失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元はファイルで、移動先はディレクトリである</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は移動先である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先がディレクトリだとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dest);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "sub"));
        Assert.Equal(dest, ex.Path);
    }

    /// <summary>
    /// 移動先の親が無いと失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動先の親ディレクトリが無い</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は移動先の親である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_親が無いとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "missing/b.txt"));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing"), ex.Path);
    }

    /// <summary>
    /// Add のあと Move は Add の対象を付け替える
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを Add している</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: pending は Add（移動先）1 件で、.txnew は元の場所に残る</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_Addのあとだと移動先のAddになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "sub/b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Add を付け替えたあと移動先を Update しても .txnew は元の場所のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと別ディレクトリへ Move している</para>
    /// <para>手順: 移動先へ UpdateAsync する</para>
    /// <para>期待: pending は Add（移動先）1 件で、.txnew は元の場所に 1 件だけある</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Addを付け替えた先でもtxnewは元の場所のままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "sub/b.txt");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.UpdateAsync("sub/b.txt", second);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        string[] sidecars = Directory.GetFiles(work.Path, "*.txnew");
        Assert.Single(sidecars);
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Add を付け替えた先の Update で journal 書き込みに失敗しても .txnew は元の場所のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと Move し、journal を排他ロックしている</para>
    /// <para>手順: 移動先へ UpdateAsync する</para>
    /// <para>期待: 例外は IOException で、pending は Add のまま、.txnew は元の場所に 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Addを付け替えた先でjournal書き込みに失敗するとtxnewは元のままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "sub/b.txt");
        await using FileStream journalLock = LockJournal(work.Path);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("sub/b.txt", second));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "sub", "b.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Update のあと Move は移動先への Add と元の Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: pending は Add と Delete で、.txnew は元の場所に残り、元ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_UpdateのあとだとAddとDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "sub/b.txt");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Move のあと Move は始点から終点へ畳む
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: B から C へ MoveAsync する</para>
    /// <para>期待: pending は Move(A→C) 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_続けてMoveすると始点から終点へ畳むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("b.txt", "c.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Move 先への Add はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: B へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になり、pending は Move のままである</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_Move先だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("b.txt", content));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Move 先への Update は移動先の Add と元の Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: B へ UpdateAsync する</para>
    /// <para>期待: pending は Add(B) と Delete(A) で、.txnew は B 側、元ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Move先だとAddとDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(dest, pending[0].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PendingChangeKind.Delete, pending[1].Kind);
        Assert.Equal(source, pending[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(dest));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Move 先への Update の未コミット Dispose では元が残り、.txnew は消える
    /// </summary>
    /// <remarks>
    /// <para>前提: Move のあと移動先を Update している</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: 元が残り、先も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Move先の未コミットDisposeでは元が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("a.txt", "b.txt");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("b.txt", content);
        }

        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Move 先への Delete は移動元の Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: B を DeleteAsync する</para>
    /// <para>期待: pending は Delete(A) 1 件で、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Move先だと元のDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Move 先を Update したあと Delete すると、Add は打ち消され元の Delete だけ残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと B を Update している</para>
    /// <para>手順: B を DeleteAsync する</para>
    /// <para>期待: pending は Delete(A) 1 件で、.txnew は無く、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Move先をUpdateしたあとだと元のDeleteだけ残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);
        await tx.DeleteAsync("b.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Move 元への Delete は Move を Delete に置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: A から B へ Move している</para>
    /// <para>手順: A を DeleteAsync する</para>
    /// <para>期待: pending は Delete(A) 1 件で、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Move元だとDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("a.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Add のあと Move でジャーナル書き込みに失敗しても pending と .txnew は元のまま残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、journal を排他ロックしている</para>
    /// <para>手順: 別ディレクトリへ MoveAsync する</para>
    /// <para>期待: 例外は IOException で、pending は Add のままで .txnew も元の場所にある</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_Addのあとでjournal書き込みに失敗するとAddのまま残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await using FileStream journalLock = LockJournal(work.Path);

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.MoveAsync("a.txt", "sub/b.txt"));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "a.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Move 先への Update でジャーナル書き込みに失敗しても pending は Move のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Move したあと、journal を排他ロックしている</para>
    /// <para>手順: 移動先へ UpdateAsync する</para>
    /// <para>期待: 例外は IOException で、pending は Move のままで .txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Move先でjournal書き込みに失敗するとMoveのまま残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using FileStream journalLock = LockJournal(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("b.txt", content));
        Assert.Null(ex.InnerException);

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(
            System.IO.Path.Combine(work.Path, "a.txt"),
            pending.Path,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
