using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitAttachTests
{
    /// <summary>
    /// Attach のコミットはファイルを残し、ジャーナルを残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Attach している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で内容があり、journal は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Attachしたファイルが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Attach のあと Update したコミットは内容を置き換える
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach のあと Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で新しい内容になる</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AttachのあとUpdateすると内容が置き換わること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Attach のあと Delete したコミットは対象を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach のあと Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AttachのあとDeleteすると対象が消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await tx.DeleteAsync("a.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// Attach のあと Move したコミットは先へ移す
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach のあと Move している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で先に内容があり、元は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AttachのあとMoveすると先へ移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await tx.MoveAsync("a.txt", "b.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
    }

    /// <summary>
    /// コミット前に内容が変わっていれば Failed で、ジャーナルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach したあと、対象を外部が書き換えている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、対象は外部の内容のまま</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_外部が内容を変えるとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        await File.WriteAllTextAsync(target, "external");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.Equal("external", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// コミット前に対象が消えていれば Failed で、ジャーナルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Attach したあと、対象を外部が消している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、対象は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_対象が消えているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("a.txt");
        File.Delete(target);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
