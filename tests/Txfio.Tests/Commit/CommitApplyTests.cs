using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitApplyTests
{
    /// <summary>
    /// Add のコミットは対象パスへ内容を昇格し、.txnew とジャーナルを消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダで Add している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で対象に内容があり、.txnew も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Addした内容が対象パスに現れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("こんにちは");
        await tx.AddAsync("a.txt", content);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Assert.Equal("こんにちは", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Update のコミットは既存ファイルを置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 内容が新しい方になり、.txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Updateは既存ファイルを置き換えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// コミット前に Add 先が外部で作られていれば Failed で対象は触らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、対象パスを外部が作成している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、対象の内容は外部が書いたまま</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Add先が外部で作られているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "external");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.Equal("external", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// コミット前に Update 対象が消えていれば Failed で、.txnew は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Update したあと、対象ファイルを外部が消している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、対象は無く .txnew は残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Update対象が消えているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        File.Delete(target);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 適用中に競合したら PartialConflict とし、ジャーナルを残す
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、対象パスがディレクトリになっていて Move できない</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: PartialConflict で、committing の journal と .txnew が残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_適用に失敗するとPartialConflictでjournalが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Directory.CreateDirectory(target);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.PartialConflict, result);
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }
}
