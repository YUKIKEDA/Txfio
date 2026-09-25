using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDeleteTreeTests
{
    /// <summary>
    /// コミットはディレクトリと配下を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ディレクトリとファイルがある木を DeleteTree している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で木も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_配下ごと消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        string child = System.IO.Path.Combine(dir, "child", "a.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(child)!);
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// 予約後に増えた子もコミットで消える
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを DeleteTree したあと、外部が子ファイルを作っている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded でディレクトリも子も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_予約後に増えた子も消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        string child = System.IO.Path.Combine(dir, "later.txt");
        await File.WriteAllTextAsync(child, "later");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(child));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// コミット前にディレクトリがファイルへ変わると Failed で、そのファイルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを DeleteTree したあと、外部が同じパスをファイルにしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、そのファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルにすり替わるとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "file");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.Equal("file", await File.ReadAllTextAsync(dir));
    }
}
