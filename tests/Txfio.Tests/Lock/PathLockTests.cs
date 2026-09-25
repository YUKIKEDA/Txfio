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
    /// <para>期待: どちらもステージングされ、ロックファイルが 3 つある</para>
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
        Assert.Equal(3, Directory.GetFiles(lockDirectory, "*.lock").Length);
        Assert.Single(first.GetPendingChanges());
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// 同じトランザクションの再ステージは競合しない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: 同じトランザクションでもう一度 Update する</para>
    /// <para>期待: 例外にならず、ロックファイルは哨兵と対象の 2 つのままである</para>
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

        Assert.Equal(2, Directory.GetFiles(System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, target))!, "*.lock").Length);
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
            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
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
    /// <para>手順: DisposeAsync し、BeginAsync する。ジャーナルを閉じてから RecoverAsync し、別トランザクションが同じパスを Add する</para>
    /// <para>期待: Dispose は IOException になり、BeginAsync は RecoveryRequiredException（Path はワークフォルダ）。Recover は RolledBack で、そのあと Add できる</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_ジャーナル削除に失敗してもロックを閉じること()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync("a.txt", content);
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        using (FileStream hold = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(hold.CanRead);
            await Assert.ThrowsAsync<IOException>(async () => await first.DisposeAsync());

            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
                () => global::Txfio.Txfio.BeginAsync(work.Path));
            Assert.Equal(work.Path, required.Path);
        }

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);

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
    /// ディレクトリ Move は他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがある</para>
    /// <para>手順: Move してから、別トランザクションがそのディレクトリを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ディレクトリはワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.MoveAsync("sub", "other");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => second.DeleteAsync("sub"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.Move, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 全削除は他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがある</para>
    /// <para>手順: DeleteTree してから、別トランザクションが別ファイルを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.DeleteTreeAsync("tree");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(() => second.DeleteAsync("a.txt"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// CreateDirectory は他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: 別ファイル a.txt がある</para>
    /// <para>手順: CreateDirectory してから、別トランザクションが a.txt を Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_ワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateDirectoryAsync("drop");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.DeleteAsync("a.txt"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(first.GetPendingChanges()).Kind);
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

    /// <summary>
    /// ファイルコピーは別パスの変更を止めない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt がある</para>
    /// <para>手順: a.txt を b.txt へコピーしてから、別トランザクションが c.txt を Add する</para>
    /// <para>期待: Add は成功する</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ファイルは別パスを止めないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CopyAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("other");

        await second.AddAsync("c.txt", content);

        Assert.Equal(PendingChangeKind.Add, Assert.Single(second.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ディレクトリコピーは他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリと、別のファイルがある</para>
    /// <para>手順: ディレクトリをコピーしてから、別トランザクションがそのファイルを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_ディレクトリはワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CopyAsync("src", "dest");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.DeleteAsync("a.txt"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ディレクトリからの ZIP 作成は他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリと、別のファイルがある</para>
    /// <para>手順: ディレクトリから ZIP を作ってから、別トランザクションがそのファイルを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ディレクトリはワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync("src", "src.zip");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.DeleteAsync("a.txt"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ZIP の展開は他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: ZIP と、別のファイルがある</para>
    /// <para>手順: ZIP を展開してから、別トランザクションがそのファイルを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task ExtractArchiveAsync_ワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, System.IO.Path.Combine(work.Path, "src.zip"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.ExtractArchiveAsync("src.zip", "dest");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.DeleteAsync("a.txt"));

        Assert.Equal(work.Path, contention.Path);
        Assert.Equal(PendingChangeKind.Add, Assert.Single(first.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ファイルだけを組で指定した ZIP 作成は、ほかのパスの変更を止めない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt がある</para>
    /// <para>手順: a.txt だけを組で指定して ZIP を作ってから、別トランザクションが b.txt を Delete する</para>
    /// <para>期待: Delete は成功し、どちらのトランザクションも操作を 1 件持つ</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ファイルだけの組はほかのパスの変更を止めないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt") }, "a.zip");

        await second.DeleteAsync("b.txt");

        Assert.Single(first.GetPendingChanges());
        Assert.Single(second.GetPendingChanges());
    }

    /// <summary>
    /// ディレクトリを含む組で指定した ZIP 作成は、他のトランザクションの変更を止める
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリと、別のファイルが 2 つある</para>
    /// <para>手順: ファイル 1 つとディレクトリを組で指定して ZIP を作ってから、別トランザクションがもう一方のファイルを Delete する</para>
    /// <para>期待: LockContentionException になり、Path はワークフォルダである</para>
    /// </remarks>
    [Fact]
    public async Task CreateArchiveAsync_ディレクトリを含む組はワークフォルダで他の変更を止めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "keep");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(work.Path);
        await first.CreateArchiveAsync(new[] { new ArchiveEntrySource("a.txt"), new ArchiveEntrySource("src") }, "all.zip");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.DeleteAsync("b.txt"));

        Assert.Equal(work.Path, contention.Path);
    }
}
