using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class LockWaitTests
{
    /// <summary>
    /// 待ちを渡さなければ、使用中のパスはすぐに失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add している</para>
    /// <para>手順: もう一方を待ちゼロで始め、同じパスを Add する</para>
    /// <para>期待: 待たずに LockContentionException になり、Path は a.txt である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_待ちがゼロなら使用中のパスですぐ失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.Zero);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

        long started = Environment.TickCount64;
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.AddAsync("a.txt", content));

        Assert.True(Environment.TickCount64 - started < 500);
        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), contention.Path);
        Assert.Empty(waiter.GetPendingChanges());
    }

    /// <summary>
    /// 待っているあいだに相手が破棄すると、その操作は成功する
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add しており、もう一方の待ちは 2 秒</para>
    /// <para>手順: 200ms 後に先のトランザクションを破棄し、同じパスを Add する</para>
    /// <para>期待: Add は成功し、未確定の操作が 1 件ある</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_待ちのあいだに相手が破棄すると成功すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (holder)
        {
            await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("a.txt", held);
            await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(2));
            Task release = Task.Run(async () =>
            {
                await Task.Delay(200);
                await holder.DisposeAsync();
            });
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

            await waiter.AddAsync("a.txt", content);
            await release;

            Assert.Single(waiter.GetPendingChanges());
        }
    }

    /// <summary>
    /// 期限までに空かなければ失敗し、次の呼び出しはまた同じ上限から待つ
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add しており、待つ側の上限は 300ms</para>
    /// <para>手順: 同じパスを Add して失敗させたあと、相手を破棄してもう一度 Add する</para>
    /// <para>期待: 1 回目は LockContentionException で Path は a.txt、2 回目は成功する</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_期限切れのあとの呼び出しはまた待てること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(300));
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("one");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.AddAsync("a.txt", first));

        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), contention.Path);
        await holder.DisposeAsync();
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("two");
        await waiter.AddAsync("a.txt", second);
        Assert.Single(waiter.GetPendingChanges());
    }

    /// <summary>
    /// 待ちの取り消しは競合にしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add しており、もう一方は期限のない待ち</para>
    /// <para>手順: 同じパスの Add を、150ms 後に取り消す</para>
    /// <para>期待: OperationCanceledException で、待つ側の未確定操作は空のまま、先のロックは残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_待ちの取り消しはOperationCanceledExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, Timeout.InfiniteTimeSpan);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        Task cancelLater = Task.Run(async () =>
        {
            await Task.Delay(150);
            cancel.Cancel();
        });
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.AddAsync("a.txt", content, cancellationToken: cancel.Token));
        await cancelLater;

        Assert.Empty(waiter.GetPendingChanges());
        await using ITransaction third = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("again");
        LockContentionException stillHeld = await Assert.ThrowsAsync<LockContentionException>(
            () => third.AddAsync("a.txt", again));
        Assert.Equal(System.IO.Path.Combine(work.Path, "a.txt"), stillHeld.Path);
    }

    /// <summary>
    /// 2 本目を待っているあいだ、先に取ったパスは持ち続ける
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、別トランザクションが m.txt を Add しており、待つ側の上限は 300ms</para>
    /// <para>手順: a.txt を m.txt へ Move する</para>
    /// <para>期待: LockContentionException で Path は m.txt、未確定操作は空で、a.txt は別トランザクションから Add できない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_2本目の期限切れでも先のロックを持ち続けること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("m.txt", held);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(300));

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.MoveAsync("a.txt", "m.txt"));

        Assert.Equal(System.IO.Path.Combine(work.Path, "m.txt"), contention.Path);
        Assert.Empty(waiter.GetPendingChanges());
        await using ITransaction third = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("again");
        await Assert.ThrowsAsync<LockContentionException>(() => third.AddAsync("a.txt", again));
    }

    /// <summary>
    /// 排他を待っているあいだはワークフォルダ全体のロックを持たないので、待っている者同士で塞がない
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add しており、sub があり、排他を待つ 2 件の上限はそれぞれ 5 秒</para>
    /// <para>手順: 2 件の DeleteTree を別スレッドで始め、先のトランザクションを破棄し、先に進んだ方を破棄してから、もう一方を待つ</para>
    /// <para>期待: どちらも 3 秒以内に成功する</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_排他を待っているあいだはワークフォルダ全体のロックを持たないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
        Task firstWait = Task.Run(async () => await first.DeleteTreeAsync("sub"));
        await Task.Delay(200);
        Task secondWait = Task.Run(async () => await second.DeleteTreeAsync("sub"));
        await Task.Delay(200);

        await holder.DisposeAsync();
        Task finished = await Task.WhenAny(firstWait, secondWait).WaitAsync(TimeSpan.FromSeconds(3));
        await finished;
        if (finished == firstWait)
        {
            await first.DisposeAsync();
            await secondWait.WaitAsync(TimeSpan.FromSeconds(3));
        }
        else
        {
            await second.DisposeAsync();
            await firstWait.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// しるしが空くと、ディレクトリ作成は成功する
    /// </summary>
    /// <remarks>
    /// <para>前提: `.txfio/share-lost.lock` を共有で開いており、待ちは 2 秒</para>
    /// <para>手順: 200ms 後にそのハンドルを閉じ、sub を CreateDirectory する</para>
    /// <para>期待: sub ができる</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_しるしが空くとディレクトリ作成が成功すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        FileStream held = new FileStream(
            MetadataNames.ShareLostLockPath(work.Path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(2));
        Task release = Task.Run(async () =>
        {
            await Task.Delay(200);
            await held.DisposeAsync();
        });

        await waiter.CreateDirectoryAsync("sub");
        await release;

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
    }

    /// <summary>
    /// しるしが期限までに空かなければ、ワークフォルダで失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: `.txfio/share-lost.lock` を共有で開いており、待ちは 300ms</para>
    /// <para>手順: sub を CreateDirectory する</para>
    /// <para>期待: LockContentionException で Path はワークフォルダ、sub はできない</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_しるしが空かないとワークフォルダで失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(MetadataNames.FolderPath(work.Path));
        await using FileStream held = new FileStream(
            MetadataNames.ShareLostLockPath(work.Path),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(300));

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => waiter.CreateDirectoryAsync("sub"));

        Assert.Equal(System.IO.Path.GetFullPath(work.Path), contention.Path);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
        _ = held;
    }

    /// <summary>
    /// パスのロックファイルを開いていても、ディレクトリ作成は失敗しない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の .lock を共有なしで開いている</para>
    /// <para>手順: sub を CreateDirectory する</para>
    /// <para>期待: sub ができる</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_パスのロックファイルだけでは失敗しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string foreign = PathLockSet.FilePath(work.Path, System.IO.Path.Combine(work.Path, "a.txt"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(foreign)!);
        await using FileStream held = new FileStream(foreign, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path);

        await waiter.CreateDirectoryAsync("sub");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "sub")));
        _ = held;
    }

    /// <summary>
    /// 復旧も、ワークフォルダ全体のロックが空くまで待つ
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションが a.txt を Add している</para>
    /// <para>手順: 待ち 300ms で Recover して失敗させ、破棄したあと 2 秒の待ちで Recover する</para>
    /// <para>期待: 1 回目は LockContentionException で Path はワークフォルダ、2 回目は NoPendingTransactions</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ワークフォルダ全体のロックが空くまで待つこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromMilliseconds(300)));

        Assert.Equal(System.IO.Path.GetFullPath(work.Path), contention.Path);
        await holder.DisposeAsync();
        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromSeconds(2));
        Assert.Equal(RecoverResult.NoPendingTransactions, report.Result);
    }

    /// <summary>
    /// 負の待ちは拒否し、期限なしは開始できる
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダがある</para>
    /// <para>手順: 負の TimeSpan で Begin と Recover を呼び、Timeout.InfiniteTimeSpan で Begin する</para>
    /// <para>期待: 負の時間は ArgumentOutOfRangeException、期限なしはトランザクションが始まる</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_負の待ちはArgumentOutOfRangeExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromMilliseconds(-2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => global::Txfio.Txfio.RecoverAsync(work.Path, TimeSpan.FromMilliseconds(-2)));
        await using ITransaction started = await global::Txfio.Txfio.BeginAsync(work.Path, Timeout.InfiniteTimeSpan);

        Assert.NotNull(started);
    }

    /// <summary>
    /// ロックを待っているあいだ、呼び出したスレッドは止まらない
    /// </summary>
    /// <remarks>
    /// <para>前提: 一方が a.txt を Add しており、もう一方の待ちは 5 秒</para>
    /// <para>手順: 同じパスへの Add を呼び、戻った Task を待たずに経過時間と状態を見る。そのあと先のトランザクションを破棄する</para>
    /// <para>期待: 呼び出しは 1 秒未満で戻り、Task は未完了のまま待っており、相手の破棄後に成功する</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_待っているあいだ呼び出したスレッドを止めないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (holder)
        {
            await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
            await holder.AddAsync("a.txt", held);
            await using ITransaction waiter = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.FromSeconds(5));
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("next");

            long started = Environment.TickCount64;
            Task waiting = waiter.AddAsync("a.txt", content);
            long returned = Environment.TickCount64 - started;

            Assert.True(returned < 1000, "呼び出しが " + returned + "ms 止まった");
            Assert.False(waiting.IsCompleted);
            await holder.DisposeAsync();
            await waiting;
            Assert.Single(waiter.GetPendingChanges());
        }
    }
}
