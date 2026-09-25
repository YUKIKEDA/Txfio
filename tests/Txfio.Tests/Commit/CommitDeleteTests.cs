using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitDeleteTests
{
    /// <summary>
    /// Delete のコミットは対象ファイルを消し、ジャーナルを残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で対象も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Deleteしたファイルが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Add を打ち消した Delete のコミットは何も作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと Delete して pending が空である</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_打ち消したAddはファイルを作らないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.DeleteAsync("a.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Delete のあと Add した Update のコミットは内容を置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: Delete のあと Add している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 対象の内容が新しい方になる</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteのあとAddすると内容が置き換わること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// コミット前に Delete 対象が消えていれば Failed で、ジャーナルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Delete したあと、対象ファイルを外部が消している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Delete対象が消えているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        File.Delete(target);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
