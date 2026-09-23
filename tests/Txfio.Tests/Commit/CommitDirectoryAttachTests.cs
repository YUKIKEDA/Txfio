using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDirectoryAttachTests
{
    /// <summary>
    /// 子が増えてもディレクトリの Attach は成功し、ディレクトリは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを Attach したあと、外部が子ファイルを作っている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded でディレクトリと子が残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_子が増えてもディレクトリが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");
        string child = System.IO.Path.Combine(dir, "later.txt");
        await File.WriteAllTextAsync(child, "later");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.True(Directory.Exists(dir));
        Assert.Equal("later", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// コミット前にディレクトリが無いと Failed になる
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを Attach したあと、外部がそれを消している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、ディレクトリは無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリが無いとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");
        Directory.Delete(dir);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// コミット前にファイルへ変わると Failed で、そのファイルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを Attach したあと、外部が同じパスをファイルにしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、そのファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルにすり替わるとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "file");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.Equal("file", await File.ReadAllTextAsync(dir));
    }

    /// <summary>
    /// 取り込んだディレクトリの Move は中身ごと移す
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを Attach してから Move している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で移動先に子があり、移動元は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Moveに畳むと中身ごと移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");
        await tx.MoveAsync("sub", "other");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(Directory.Exists(dir));
        Assert.Equal("moved", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "other", "a.txt")));
    }
}
