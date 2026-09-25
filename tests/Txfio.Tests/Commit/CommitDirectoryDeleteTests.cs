using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDirectoryDeleteTests
{
    /// <summary>
    /// 空ディレクトリのコミットは対象を消し、ジャーナルを残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded でディレクトリも journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_空ディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// 子と親を Delete したコミットは両方消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 直下ファイルと親ディレクトリを Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で子も親も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_子と親のDeleteで両方消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        string child = System.IO.Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub/a.txt");
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(child));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// Move 出しのあと親を Delete したコミットは先へ移し、元のディレクトリを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 直下ファイルを外へ Move し、親を Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 先に内容があり、元のディレクトリは無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Move出しのあと親が消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub/a.txt", "b.txt");
        await tx.DeleteAsync("sub");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(Directory.Exists(dir));
        Assert.Equal("moved", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// コミット前に直下へ外部がファイルを足すと Failed で、ディレクトリは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Delete したあと、外部が直下にファイルを作っている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_外部が子を足すとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "external.txt"), "no");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.True(Directory.Exists(dir));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
