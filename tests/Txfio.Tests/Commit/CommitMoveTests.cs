using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitMoveTests
{
    /// <summary>
    /// Move のコミットはファイルを移動し、ジャーナルを残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Move している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で先の内容があり、元も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Moveしたファイルが先へ移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Add を付け替えた Move のコミットは移動先だけ作る
    /// </summary>
    /// <remarks>
    /// <para>前提: Add のあと Move している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で先だけあり、元は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddのあとMoveすると先だけ作られること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// Update のあと Move したコミットは先の内容を置き換え、元を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Update のあと Move している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 先が新しい内容で、元は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateのあとMoveすると先が新しい内容になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);
        await tx.MoveAsync("a.txt", "b.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
    }

    /// <summary>
    /// 畳んだ Move のコミットは始点から終点へ移す
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと Move(B→C) している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: C に内容があり A も B も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_畳んだMoveは終点へ移ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.MoveAsync("b.txt", "c.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "c.txt")));
    }

    /// <summary>
    /// Move のあと移動先を Update したコミットは先の内容を置き換え、元を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと B を Update している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 先が新しい内容で、元は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveのあとUpdateすると先が新しい内容になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.Equal("new", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// Move のあと移動先を Delete したコミットは元を消し、先は作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと B を Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 元も先も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Moveのあと先をDeleteすると元が消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Move のあと移動元を Delete したコミットは元を消し、先は作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと A を Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 元も先も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Moveのあと元をDeleteすると元が消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("a.txt");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// コミット前に移動元が消えていれば Failed で、ジャーナルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Move したあと、移動元を外部が消している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_移動元が消えているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        File.Delete(source);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// コミット前に移動先ができていれば Failed で、ジャーナルは残る
    /// </summary>
    /// <remarks>
    /// <para>前提: Move したあと、移動先を外部が作っている</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Failed で、元は残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_移動先ができているとFailedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(dest, "external");

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Equal("external", await File.ReadAllTextAsync(dest));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }
}
