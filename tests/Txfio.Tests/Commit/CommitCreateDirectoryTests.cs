using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitCreateDirectoryTests
{
    /// <summary>
    /// コミットはディレクトリと、素のファイル API で書いた中身を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、素のファイル API で子ファイルを書いている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded でディレクトリと子が残り、journal は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ディレクトリと中身が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string child = System.IO.Path.Combine(work.Path, "drop", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(child, "keep");

        CommitResult result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// コミット前にディレクトリがファイルへ変わると Failed で、そのファイルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、同じパスをファイルにしている</para>
    /// <para>手順: CommitAsync してから破棄する</para>
    /// <para>期待: Failed で、そのファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ファイルにすり替わるとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            Directory.Delete(dir);
            await File.WriteAllTextAsync(dir, "file");

            CommitResult result = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, result);
        }

        Assert.Equal("file", await File.ReadAllTextAsync(dir));
    }

    /// <summary>
    /// 他の操作の検証失敗でも、作ったディレクトリは破棄で消える
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory と、コミット前に消されたファイルの Update がある</para>
    /// <para>手順: CommitAsync してから破棄する</para>
    /// <para>期待: Failed で、drop と中のファイルが無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_検証失敗の破棄でディレクトリが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string child = System.IO.Path.Combine(work.Path, "drop", "b.txt");
        await File.WriteAllTextAsync(updated, "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            await File.WriteAllTextAsync(child, "gone");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("a.txt", content);
            File.Delete(updated);

            CommitResult result = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, result);
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.False(File.Exists(child));
    }

    /// <summary>
    /// 配下の Add はコミットで本物のパスへ残り、ディレクトリも残る
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory し、drop/a.txt を Add している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で a.txt が残り、.txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_配下のAddが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string child = System.IO.Path.Combine(work.Path, "drop", "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await tx.WriteAllTextAsync("drop/a.txt", "staged");

        CommitResult result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("staged", await File.ReadAllTextAsync(child));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, "drop"), "*.txnew"));
    }
}
