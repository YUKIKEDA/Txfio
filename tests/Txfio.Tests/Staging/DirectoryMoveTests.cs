using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DirectoryMoveTests
{
    /// <summary>
    /// 中身のあるディレクトリはコミットで移り、コミット前は動かない
    /// </summary>
    /// <remarks>
    /// <para>前提: sub の中に a.txt と nested/b.txt がある</para>
    /// <para>手順: other へ Move し、コミットする</para>
    /// <para>期待: コミット前は sub のまま、コミット後は other に同じ内容があり sub は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_コミットでディレクトリとその中身が移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        string nested = System.IO.Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "hello");
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "b.txt"), "deep");
        string dest = System.IO.Path.Combine(work.Path, "other");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");

        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(dest));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(dest, "a.txt")));
        Assert.Equal("deep", await File.ReadAllTextAsync(System.IO.Path.Combine(dest, "nested", "b.txt")));
    }

    /// <summary>
    /// 未コミットの Dispose ではディレクトリは動かない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の sub がある</para>
    /// <para>手順: Move して Dispose する</para>
    /// <para>期待: sub が残り、other は無い</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_未コミットDisposeではディレクトリが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("sub", "other");
        }

        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
    }

    /// <summary>
    /// 移動先が塞がっていると ExternalConflictException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: sub があり、other はファイル、taken はディレクトリである</para>
    /// <para>手順: それぞれへ Move する</para>
    /// <para>期待: どちらも ExternalConflictException で、Path は移動先、sub は残る</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先が塞がっているとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        string file = System.IO.Path.Combine(work.Path, "other");
        await File.WriteAllTextAsync(file, "keep");
        string directory = System.IO.Path.Combine(work.Path, "taken");
        Directory.CreateDirectory(directory);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException fileConflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.MoveAsync("sub", "other"));
        ExternalConflictException directoryConflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.MoveAsync("sub", "taken"));

        Assert.Equal(file, fileConflict.Path);
        Assert.Equal(directory, directoryConflict.Path);
        Assert.Empty(tx.GetPendingChanges());
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
    }

    /// <summary>
    /// 大文字小文字だけが違うディレクトリ Move は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の sub がある</para>
    /// <para>手順: Sub へ Move し、続けて sub から sub へ Move する</para>
    /// <para>期待: どちらも InvalidOperationException で、pending は空、ロックは無く、sub が残る</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ディレクトリの大文字小文字だけが違うとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        InvalidOperationException differentCase = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.MoveAsync("sub", "Sub"));
        InvalidOperationException same = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.MoveAsync("sub", "sub"));

        Assert.Contains("同じパスへは移動できません", differentCase.Message, StringComparison.Ordinal);
        Assert.Contains("同じパスへは移動できません", same.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        Assert.True(Directory.Exists(source));
    }

    /// <summary>
    /// 自分自身の配下へは移せず、配下に操作があると失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: sub の中に a.txt がある</para>
    /// <para>手順: sub を sub/inner へ Move し、a.txt を Add したあと sub を Move する</para>
    /// <para>期待: どちらも InvalidOperationException で、先の Move はロックを取らず、あとの pending は Add のままである</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("sub", "sub/inner"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        await using MemoryStream content = new MemoryStream("new"u8.ToArray());
        await tx.AddAsync("sub/b.txt", content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("sub", "other"));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 続けて Move すると始点から終点へ畳む
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の sub がある</para>
    /// <para>手順: sub を mid へ、mid を final へ Move してコミットする</para>
    /// <para>期待: pending は sub から final の 1 件で、コミット後は final だけがある</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_続けてMoveすると始点から終点へ畳むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        string dest = System.IO.Path.Combine(work.Path, "final");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "mid");
        await tx.MoveAsync("mid", "final");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, pending.NewPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "mid")));
        Assert.True(Directory.Exists(dest));
    }

    /// <summary>
    /// 空のディレクトリ Move のあとの Delete は元の削除になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の sub を other へ Move している</para>
    /// <para>手順: other を Delete してコミットする</para>
    /// <para>期待: pending は sub の Delete で、コミット後に sub も other も無い</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_空のディレクトリMoveのあとは元のDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");
        await tx.DeleteAsync("other");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(source, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
    }

    /// <summary>
    /// 中身があるディレクトリ Move のあとの Delete は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある sub を other へ Move している</para>
    /// <para>手順: other を Delete する</para>
    /// <para>期待: ExternalConflictException になり、pending は Move のままである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_中身があるディレクトリMoveのあとはExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("other"));

        Assert.Equal(source, conflict.Path);
        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 木の外の Add はディレクトリ Move のあとでも積める
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の sub を Move している</para>
    /// <para>手順: c.txt を Add してコミットする</para>
    /// <para>期待: 未確定操作は 2 件で、コミット後に other と c.txt がある</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ディレクトリMoveのあと木の外は積めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub", "other");
        await using MemoryStream content = new MemoryStream("out"u8.ToArray());
        await tx.AddAsync("c.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
        Assert.Equal("out", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// 先にパスを押さえているとディレクトリ Move は積まない
    /// </summary>
    /// <remarks>
    /// <para>前提: 別トランザクションが a.txt を Add し、sub がある</para>
    /// <para>手順: sub を Move する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダ、pending は空である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_他のトランザクションがパスを押さえるとLockContentionExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = new MemoryStream("a"u8.ToArray());
        await holder.AddAsync("a.txt", content);
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => mover.MoveAsync("sub", "other"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Empty(mover.GetPendingChanges());
    }

    /// <summary>
    /// 使用中の別ロックがあると排他をやめて共有に戻す
    /// </summary>
    /// <remarks>
    /// <para>前提: 哨兵を排他で持ち、別の .lock を共有なしで開いている</para>
    /// <para>手順: 自分以外のロックを確認する</para>
    /// <para>期待: LockContentionException で Path はワークフォルダ、そのあと別の集合は共有を取れ、排他は取れない</para>
    /// </remarks>
    [Fact]
    public async Task RejectForeignLocks_使用中なら共有に戻ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string foreign = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(foreign)!);
        using FileStream held = new FileStream(foreign, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        PathLockSet mover = new PathLockSet();
        mover.AcquireExclusive(work.Path);

        LockContentionException contention = Assert.Throws<LockContentionException>(() => mover.RejectForeignLocks(work.Path));

        Assert.Equal(work.Path, contention.Path);
        PathLockSet other = new PathLockSet();
        other.AcquireShared(work.Path);
        Assert.Throws<LockContentionException>(() => other.AcquireExclusive(work.Path));
        mover.Release();
        other.Release();
    }

    /// <summary>
    /// 排他への昇格に失敗しても、共有は持ち続ける
    /// </summary>
    /// <remarks>
    /// <para>前提: 2つの集合が哨兵を共有で持っている</para>
    /// <para>手順: 片方を排他にする。失敗したあと、もう片方を解放し、第三者が排他を取る</para>
    /// <para>期待: 昇格は LockContentionException で、第三者も排他を取れない</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_昇格に失敗しても共有を持ち続けること()
    {
        await using TempDirectory work = TempDirectory.Create();
        PathLockSet holder = new PathLockSet();
        PathLockSet mover = new PathLockSet();
        holder.AcquireShared(work.Path);
        mover.AcquireShared(work.Path);

        LockContentionException contention = Assert.Throws<LockContentionException>(() => mover.AcquireExclusive(work.Path));

        Assert.Equal(work.Path, contention.Path);
        holder.Release();
        PathLockSet third = new PathLockSet();
        Assert.Throws<LockContentionException>(() => third.AcquireExclusive(work.Path));
        mover.Release();
        third.Release();
    }

    /// <summary>
    /// 共有へ戻せない IO 失敗は握りつぶさず、次の取得で開き直す
    /// </summary>
    /// <remarks>
    /// <para>前提: 哨兵を共有で持っている。昇格は共有違反、戻しは別の IOException</para>
    /// <para>手順: 排他を取る。失敗指定を消してから共有を取り直す</para>
    /// <para>期待: 戻しの IOException がそのまま出る。取り直したあとは別の集合が排他を取れない</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_共有へ戻せない失敗は次の取得で開くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        PathLockSet mover = new PathLockSet();
        mover.AcquireShared(work.Path);
        PathLockSet.FailNextOpen(FileShare.None, SharingViolation());
        PathLockSet.FailNextOpen(FileShare.ReadWrite, new IOException("disk"));
        try
        {
            IOException failure = Assert.Throws<IOException>(() => mover.AcquireExclusive(work.Path));
            Assert.Equal("disk", failure.Message);
        }
        finally
        {
            PathLockSet.ClearOpenFailures();
        }

        mover.AcquireShared(work.Path);
        PathLockSet other = new PathLockSet();
        Assert.Throws<LockContentionException>(() => other.AcquireExclusive(work.Path));
        mover.Release();
        other.Release();
    }

    /// <summary>
    /// 共有へ戻せなかったあとの排他は、先に共有を開き直す
    /// </summary>
    /// <remarks>
    /// <para>前提: 昇格も共有への戻しも共有違反で失敗している</para>
    /// <para>手順: 失敗指定を消し、別の集合が共有を持っているあいだに排他を取り直す。その集合を解放してから第三者が排他を取る</para>
    /// <para>期待: 取り直しは LockContentionException で、第三者も排他を取れない</para>
    /// </remarks>
    [Fact]
    public async Task AcquireExclusive_哨兵を失ったあとは共有を戻してから排他を試すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        PathLockSet mover = new PathLockSet();
        mover.AcquireShared(work.Path);
        PathLockSet.FailNextOpen(FileShare.None, SharingViolation());
        PathLockSet.FailNextOpen(FileShare.ReadWrite, SharingViolation());
        try
        {
            Assert.Throws<LockContentionException>(() => mover.AcquireExclusive(work.Path));
        }
        finally
        {
            PathLockSet.ClearOpenFailures();
        }

        PathLockSet holder = new PathLockSet();
        holder.AcquireShared(work.Path);
        Assert.Throws<LockContentionException>(() => mover.AcquireExclusive(work.Path));
        holder.Release();
        PathLockSet third = new PathLockSet();
        Assert.Throws<LockContentionException>(() => third.AcquireExclusive(work.Path));
        mover.Release();
        third.Release();
    }

    private static IOException SharingViolation()
    {
        IOException exception = new IOException("sharing");
        exception.HResult = unchecked((int)0x80070020);
        return exception;
    }
}
