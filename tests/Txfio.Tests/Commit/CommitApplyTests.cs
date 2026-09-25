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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);

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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Update のあと外部が内容を変えても、Commit 直前のディスクを Before にして成功する
    /// </summary>
    /// <remarks>
    /// <para>前提: Update したあと、対象の内容を外部が変えている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で対象は Update の内容</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Update後に内容が変わってもSucceededになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await File.WriteAllTextAsync(target, "external");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Add の対象が検証時に既にあると Failed になり、実体は触らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、対象パスをディレクトリにしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、ディレクトリと .txnew は残り、journal は Committing にならない</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Add対象が既にあるとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Directory.CreateDirectory(target);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.True(Directory.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        string journal = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.Contains("\"committing\":false", await File.ReadAllTextAsync(journal), StringComparison.Ordinal);
    }

    /// <summary>
    /// Add と Update と Delete を混ぜたコミットはすべて反映する
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルの Update、新規 Add、別ファイルの Delete をしている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で Update と Add の内容があり、Delete 対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddとUpdateとDeleteを混ぜても反映すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "gone.txt");
        await File.WriteAllTextAsync(updated, "old");
        await File.WriteAllTextAsync(deleted, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream updateContent = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", updateContent);
        await using MemoryStream addContent = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("b.txt", addContent);
        await tx.DeleteAsync("gone.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(updated));
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(deleted));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 検証は通るが Update の適用が失敗したら PartialConflict とし、ジャーナルと .txnew を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Update したあと、対象ファイルを共有読み取りで開いたままにしている</para>
    /// <para>手順: CommitAsync し、そのあと別のトランザクションを開始する</para>
    /// <para>期待: PartialConflict で、journal と .txnew は残らず、対象は元の内容。次のトランザクションは開始できる</para>
    /// </remarks>
    [WindowsFact("開いたファイルは rename できない")]
    public async Task CommitAsync_適用に失敗するとPartialConflictでjournalを消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.UpdateAsync("a.txt", content);
        await using FileStream locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.PartialConflict, result.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }
}
