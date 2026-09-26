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
    /// <para>期待: pending は Add（移動先）1 件で、.txnew は移動先の名前で移動先のディレクトリにある</para>
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
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
    }

    /// <summary>
    /// Add を付け替えたあと移動先を Update すると、移動先の .txnew を書き直す
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと別ディレクトリへ Move している</para>
    /// <para>手順: 移動先へ UpdateAsync する</para>
    /// <para>期待: pending は Add（移動先）1 件で、.txnew は移動先のディレクトリに 1 件だけあり、新しい内容である</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Addを付け替えた先のtxnewを書き直すこと()
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
        string[] sidecars = Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew");
        Assert.Single(sidecars);
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Add を付け替えた先の Update で journal 書き込みに失敗しても、移動先の .txnew は前の内容のままである
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと Move し、journal を排他ロックしている</para>
    /// <para>手順: 移動先へ UpdateAsync する</para>
    /// <para>期待: 例外は IOException で、pending は Add のまま、.txnew は移動先のディレクトリに 1 件で前の内容である</para>
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
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
        Assert.Equal(
            "first",
            await File.ReadAllTextAsync(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew")[0]));
    }

    /// <summary>
    /// Update のあと Move は移動先への Add と元の Delete になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: MoveAsync する</para>
    /// <para>期待: pending は Add と Delete で、.txnew は移動先のディレクトリへ移り、元ファイルは残る</para>
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
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, "sub"), "*.txnew"));
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

    /// <summary>
    /// 大文字小文字だけが違う Move は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: A.txt へ Move する</para>
    /// <para>期待: InvalidOperationException で、pending は空、ロックは無く、a.txt が残り内容も変わらない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_大文字小文字だけが違うとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "A.txt"));

        Assert.Contains("同じパスへは移動できません", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.Equal("a.txt", System.IO.Path.GetFileName(Assert.Single(Directory.GetFiles(work.Path, "a.txt"))));
    }

    /// <summary>
    /// 完全に同じパスへの Move は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: a.txt から a.txt へ Move する</para>
    /// <para>期待: InvalidOperationException で、pending は空である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_同じパスだとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "a.txt"));

        Assert.Contains("同じパスへは移動できません", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 別パスへの Move を表記だけ変えてもう一度呼んでも、元の予約のまま残る
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を b.txt へ Move してある</para>
    /// <para>手順: a.txt を B.txt へ Move する</para>
    /// <para>期待: 例外にならず、pending は a.txt から b.txt の 1 件のままである</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_同じ移動先を表記だけ変えても予約は変わらないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        await tx.MoveAsync("a.txt", "B.txt");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal("a.txt", System.IO.Path.GetFileName(pending.Path), StringComparer.Ordinal);
        Assert.Equal("b.txt", System.IO.Path.GetFileName(pending.NewPath), StringComparer.Ordinal);
    }

    /// <summary>
    /// 置き換えの Move は、コミットで移動先の既存ファイルを移動元で置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt がある</para>
    /// <para>手順: Move(a.txt→b.txt, overwrite: true) を予約し、コミット前後のディスクを見る</para>
    /// <para>期待: コミット前は両方とも元のまま、コミット後は b.txt が旧 a.txt の中身であり、a.txt は無く、.txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_overwriteなら移動先を置き換えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(dest, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("old", await File.ReadAllTextAsync(dest));
        Assert.Equal("new", await tx.ReadAllTextAsync("b.txt"));
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 移動先の Delete は、置き換えの Move に畳む
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt がある</para>
    /// <para>手順: Delete(b.txt) のあと Move(a.txt→b.txt, overwrite: true) してコミットする</para>
    /// <para>期待: 未確定の操作は Move 1 件であり、コミット後の b.txt は旧 a.txt の中身</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先のDeleteを置き換えのMoveに畳むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.DeleteAsync("b.txt");
        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        PendingChange change = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, change.Kind);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 移動先が無ければ、置き換えの指定があっても普通の Move になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、b.txt は無い</para>
    /// <para>手順: Move(a.txt→b.txt, overwrite: true) してジャーナルを読み、コミットする</para>
    /// <para>期待: ジャーナルに overwrite は無く、コミット後は b.txt がある</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先が無ければ普通のMoveになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        Assert.DoesNotContain("overwrite", await File.ReadAllTextAsync(journal), StringComparison.Ordinal);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 置き換えの Move でも、移動先がディレクトリなら失敗し、ディレクトリの Move は未対応
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt、ディレクトリ d と e がある</para>
    /// <para>手順: Move(a.txt→d, overwrite: true) と Move(d→e, overwrite: true) をする</para>
    /// <para>期待: 前者は ExternalConflictException、後者は UnsupportedOperationException であり、操作は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_overwriteでもディレクトリは置き換えないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "e"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "d", overwrite: true));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.MoveAsync("d", "e", overwrite: true));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 置き換えの Move の移動元と移動先へは、続けて操作できない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、Move(a.txt→b.txt, overwrite: true) を予約した</para>
    /// <para>手順: b.txt への書き込み、a.txt への書き込み、b.txt の Delete、b.txt の Move をする</para>
    /// <para>期待: どれも InvalidOperationException であり、操作は Move 1 件のまま</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_置き換えのMoveの元と先へは続けて操作できないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("b.txt", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.WriteAllTextAsync("a.txt", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync("b.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("b.txt", "c.txt"));

        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// ステージ済みの Add を置き換えの Move で動かすと、移動先の Update になる
    /// </summary>
    /// <remarks>
    /// <para>前提: b.txt がある</para>
    /// <para>手順: a.txt を Add し、Move(a.txt→b.txt, overwrite: true) してコミットする</para>
    /// <para>期待: 未確定の操作は b.txt の Update 1 件であり、コミット後の b.txt は Add した中身</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ステージ済みのAddを置き換えると移動先のUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "staged");

        await tx.MoveAsync("a.txt", "b.txt", overwrite: true);

        PendingChange change = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Update, change.Kind);
        Assert.Equal(dest, change.Path);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(dest));
    }

    /// <summary>
    /// Move 先への Update は、移動先の .txnew を書く前にジャーナルへ載せる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、Move(a.txt→b.txt) を予約した</para>
    /// <para>手順: b.txt を UpdateAsync し、書き込み中の進捗でジャーナルを読む</para>
    /// <para>期待: 進捗が届いたどの時点でも、ジャーナルに b.txt の .txnew が書いてあり、最後は Add と Delete に畳まれる</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_Move先への書き込みはtxnewより先にジャーナルへ載せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        JournalProbeProgress probe = new JournalProbeProgress(work.Path, "b.txt.");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.UpdateAsync("b.txt", content, probe);

        Assert.True(probe.Reported);
        Assert.True(probe.AlwaysJournaled);
        Assert.Equal(
            new[] { PendingChangeKind.Add, PendingChangeKind.Delete },
            tx.GetPendingChanges().Select(static change => change.Kind).OrderBy(static kind => kind).ToArray());
    }

    /// <summary>
    /// ステージ済みの Add を Move すると、.txnew は移動先の名前に付け替わり、ジャーナルもそれを指す
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add した</para>
    /// <para>手順: Move(a.txt→c.txt) する</para>
    /// <para>期待: .txnew は c.txt の名前の 1 つだけで、ジャーナルは c.txt の .txnew を指し、a.txt の .txnew を指さない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ステージ済みのAddはtxnewとジャーナルを移動先へ付け替えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        await tx.MoveAsync("a.txt", "c.txt");

        string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.StartsWith("c.txt.", System.IO.Path.GetFileName(staging), StringComparison.Ordinal);
        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string text = await File.ReadAllTextAsync(journal);
        Assert.Contains(System.IO.Path.GetFileName(staging), text, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt.", text, StringComparison.Ordinal);
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
