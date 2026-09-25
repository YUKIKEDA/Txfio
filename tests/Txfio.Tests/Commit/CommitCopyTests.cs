using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitCopyTests
{
    /// <summary>
    /// ファイルのコピーはコミットでコピー先を作り、コピー元を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を b.txt へコピーしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で両方に同じ内容がある</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルのコピーは両方残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "copied");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CopyAsync("a.txt", "b.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("copied", await File.ReadAllTextAsync(source));
        Assert.Equal("copied", await File.ReadAllTextAsync(destination));
    }

    /// <summary>
    /// ディレクトリのコピーはコミットで中身と空ディレクトリを残す
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルと空のサブディレクトリがあるディレクトリをコピーしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded でコピー先にファイルと空ディレクトリがあり、コピー元も残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリのコピーは空ディレクトリも残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "copied");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CopyAsync("src", "dest");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("copied", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal("copied", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Empty(Directory.GetFiles(destination, "*.txnew", SearchOption.AllDirectories));
    }
}
