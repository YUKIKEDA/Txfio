using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class IntentLockTests
{
    /// <summary>
    /// 先に配下をロックしてから予約すると、失敗したパスは予約するディレクトリである
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがあり、別トランザクションがその直下を Add している</para>
    /// <para>手順: そのディレクトリを DeleteTree する</para>
    /// <para>期待: LockContentionException になり、Path はそのディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_配下を先にロックしているとディレクトリで失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        await holder.AddAsync("tree/a.txt", content);

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => deleter.DeleteTreeAsync("tree"));

        Assert.Equal(tree, contention.Path);
        Assert.Empty(deleter.GetPendingChanges());
    }

    /// <summary>
    /// 予約したあとに配下を触ると、失敗したパスは予約したディレクトリである
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを DeleteTree している</para>
    /// <para>手順: 別トランザクションがその直下を Add する</para>
    /// <para>期待: LockContentionException になり、Path はそのディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_予約のあと配下を触るとディレクトリで失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await deleter.DeleteTreeAsync("tree");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("tree/a.txt", content));

        Assert.Equal(tree, contention.Path);
        Assert.Empty(child.GetPendingChanges());
    }

    /// <summary>
    /// ワークフォルダ全体を排他にできなくても、取った意図ロックは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 別トランザクションが a.txt を Add しており、tree がある</para>
    /// <para>手順: tree の DeleteTree が失敗したあと、第三者が tree の配下を Add する</para>
    /// <para>期待: DeleteTree の Path はワークフォルダ、配下の Add の Path は tree である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_排他に上げる前に失敗しても意図ロックは残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(tree);
        await using ITransaction holder = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction deleter = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream held = LeftoverAddFiles.Utf8Stream("held");
        await holder.AddAsync("a.txt", held);

        LockContentionException folder = await Assert.ThrowsAsync<LockContentionException>(
            () => deleter.DeleteTreeAsync("tree"));
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");
        LockContentionException reserved = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("tree/b.txt", content));

        Assert.Equal(work.Path, folder.Path);
        Assert.Equal(tree, reserved.Path);
        Assert.Empty(deleter.GetPendingChanges());
    }

    /// <summary>
    /// ディレクトリ Move の移動先も、戻ったあと予約されたままである
    /// </summary>
    /// <remarks>
    /// <para>前提: sub がある</para>
    /// <para>手順: sub を other へ Move してから、別トランザクションが other の配下を Add する</para>
    /// <para>期待: LockContentionException になり、Path は other である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先も戻ったあと予約されること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dest = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction mover = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using ITransaction child = await global::Txfio.Txfio.BeginAsync(work.Path);
        await mover.MoveAsync("sub", "other");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("child");

        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => child.AddAsync("other/a.txt", content));

        Assert.Equal(dest, contention.Path);
    }

    /// <summary>
    /// 意図ロックのファイルは、同じパスのパスロックとは別である
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダと、その直下のパスがある</para>
    /// <para>手順: パスロックと意図ロックのパスを求める</para>
    /// <para>期待: 2 つのパスは異なり、どちらも .lock で終わる</para>
    /// </remarks>
    [Fact]
    public void IntentFilePath_パスロックとは別ファイルであること()
    {
        string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-intent");
        string target = System.IO.Path.Combine(work, "sub");

        string pathLock = PathLockSet.FilePath(work, target);
        string intent = PathLockSet.IntentFilePath(work, target);

        Assert.NotEqual(pathLock, intent);
        Assert.EndsWith(".lock", pathLock, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".lock", intent, StringComparison.OrdinalIgnoreCase);
    }
}
