using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class PathLockTests
{
    /// <summary>
    /// 別トランザクションは同じパスをロックできない
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方のトランザクションが Add している</para>
    /// <para>手順: もう一方が同じパスと、大文字だけ変えたパスを Add する</para>
    /// <para>期待: どちらも LockContentionException になり、Path はそれぞれの Add 対象パスである</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_他のトランザクションが同じパスを使うとLockContentionExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string lower = System.IO.Path.Combine(work.Path, "a.txt");
        string upper = System.IO.Path.Combine(work.Path, "A.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException same = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("a.txt", again));
        Assert.Equal(lower, same.Path);

        await using MemoryStream cased = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException differentCase = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("A.txt", cased));
        Assert.Equal(upper, differentCase.Path);
    }

    /// <summary>
    /// 別パスは同時にステージングできる
    /// </summary>
    /// <remarks>
    /// <para>前提: 2つのトランザクションを開始している</para>
    /// <para>手順: それぞれ別のパスを Add する</para>
    /// <para>期待: どちらもステージングされ、ロックファイルが2つある</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_別パスは同時にステージングできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream left = LeftoverAddFiles.Utf8Stream("a");
        await using MemoryStream right = LeftoverAddFiles.Utf8Stream("b");
        await first.AddAsync("a.txt", left);
        await second.AddAsync("b.txt", right);

        string lockDirectory = System.IO.Path.GetDirectoryName(
            PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt")))!;
        Assert.Equal(2, Directory.GetFiles(lockDirectory, "*.lock").Length);
        Assert.Single(first.GetPendingChanges());
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// 同じトランザクションの再ステージは競合しない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: 同じトランザクションでもう一度 Update する</para>
    /// <para>期待: 例外にならず、ロックファイルは1つのままである</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_同じトランザクションの再ステージは競合しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("one");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("two");
        await tx.UpdateAsync("a.txt", first);
        await tx.UpdateAsync("a.txt", second);

        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, target))!, "*.lock"));
        Assert.Single(tx.GetPendingChanges());
    }

    /// <summary>
    /// コミット後は別トランザクションがロックでき、.lock は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Add をコミットしている</para>
    /// <para>手順: 別トランザクションが同じパスを Update する</para>
    /// <para>期待: Update でき、.lock ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_あとには別トランザクションが同じパスをロックできファイルは残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        }

        Assert.True(File.Exists(lockFile));
        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream update = LeftoverAddFiles.Utf8Stream("next");
        await next.UpdateAsync("a.txt", update);
        Assert.Single(next.GetPendingChanges());
    }

    /// <summary>
    /// Dispose 後は別トランザクションがロックでき、.lock は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したトランザクションを Dispose している</para>
    /// <para>手順: 別トランザクションが同じパスを Add する</para>
    /// <para>期待: Add でき、.lock ファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_あとには別トランザクションが同じパスをロックできファイルは残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string lockFile = PathLockSet.FilePath(work.Path, target);
        ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);
        await first.DisposeAsync();

        Assert.True(File.Exists(lockFile));
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        await second.AddAsync("a.txt", again);
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// ロールバック中にジャーナル削除が失敗してもロックは閉じる
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、ジャーナルを共有なしで開いている</para>
    /// <para>手順: DisposeAsync する</para>
    /// <para>期待: IOException になり、別トランザクションが同じパスを Add できる</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_ジャーナル削除に失敗してもロックを閉じること()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        using FileStream hold = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(async () => await first.DisposeAsync());

        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        await second.AddAsync("a.txt", again);
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// 親が無い Add のあとも、そのパスは押さえられたままである
    /// </summary>
    /// <remarks>
    /// <para>前提: 親ディレクトリが無い</para>
    /// <para>手順: Add してから、別トランザクションが同じパスを Add する</para>
    /// <para>期待: 先は ExternalConflictException、後は LockContentionException である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_親が無くてもロックは残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "missing", "a.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<ExternalConflictException>(() => first.AddAsync("missing/a.txt", content));

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("missing/a.txt", again));
        Assert.Equal(target, contention.Path);
    }

    /// <summary>
    /// Move は移動元と移動先の両方をロックする
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元のファイルがある</para>
    /// <para>手順: Move したあと、別トランザクションが移動元を Delete し、移動先を Add する</para>
    /// <para>期待: どちらも LockContentionException になり、Path はそれぞれの対象パスである</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動元と移動先の両方をロックすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "src");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.MoveAsync("a.txt", "b.txt");

        LockContentionException sourceLock = await Assert.ThrowsAsync<LockContentionException>(() => second.DeleteAsync("a.txt"));
        Assert.Equal(source, sourceLock.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        LockContentionException destLock = await Assert.ThrowsAsync<LockContentionException>(() => second.AddAsync("b.txt", content));
        Assert.Equal(dest, destLock.Path);
    }

    /// <summary>
    /// Move の2本目のロックが取れなくても、1本目は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動先を別トランザクションが Update している</para>
    /// <para>手順: その移動先へ Move し、さらに別トランザクションが移動元を Delete する</para>
    /// <para>期待: Move は移動先で LockContentionException になり、移動元のロックもまだ取れない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_2本目が取れなくても1本目は残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "held.txt");
        await File.WriteAllTextAsync(source, "src");
        await File.WriteAllTextAsync(dest, "held");
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction other = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await holder.UpdateAsync("held.txt", content);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => mover.MoveAsync("a.txt", "held.txt"));
        Assert.Equal(dest, contention.Path);
        LockContentionException sourceLock = await Assert.ThrowsAsync<LockContentionException>(() => other.DeleteAsync("a.txt"));
        Assert.Equal(source, sourceLock.Path);
    }

    /// <summary>
    /// ディレクトリ Delete は子ファイルをロックしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のディレクトリを Delete 予約している</para>
    /// <para>手順: 別トランザクションがその直下を Add する</para>
    /// <para>期待: Add できる</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_ディレクトリをロックしても子は別トランザクションが触れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.DeleteAsync("sub");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        await second.AddAsync("sub/a.txt", content);
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// ディレクトリ Move は新しいロックを取らない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがある</para>
    /// <para>手順: Move してから、別トランザクションがそのディレクトリを Delete する</para>
    /// <para>期待: Move は UnsupportedOperationException になり、その時点ではロックフォルダが無く、そのあと Delete は予約できる</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ディレクトリでは新しいロックを取らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => first.MoveAsync("sub", "other"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
        await second.DeleteAsync("sub");
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// 反対方向の Move を同時に呼んでも止まらない
    /// </summary>
    /// <remarks>
    /// <para>前提: 移動元 a.txt があり、b.txt は無い</para>
    /// <para>手順: Move(a→b) と Move(b→a) を同時に開始して完了を待つ</para>
    /// <para>期待: 5秒以内に終わり、一方は LockContentionException である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_反対方向を同時に呼んでも止まらないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        Task forward = first.MoveAsync("a.txt", "b.txt");
        Task backward = second.MoveAsync("b.txt", "a.txt");
        Task both = Task.WhenAll(forward, backward);
        Task finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(both, finished);

        int contentions = 0;
        if (forward.IsFaulted && forward.Exception!.InnerException is LockContentionException)
        {
            contentions++;
        }

        if (backward.IsFaulted && backward.Exception!.InnerException is LockContentionException)
        {
            contentions++;
        }

        Assert.Equal(1, contentions);
    }
}
