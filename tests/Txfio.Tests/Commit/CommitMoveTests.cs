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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// Move 先を Update したあと Delete したコミットは元を消し、先は作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Move(A→B) のあと B を Update し、さらに B を Delete している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: 元も先も無く、journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Move先をUpdateしたあとDeleteすると元が消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("b.txt", content);
        await tx.DeleteAsync("b.txt");

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
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

        CommitReport result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Failed, result.Result);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Equal("external", await File.ReadAllTextAsync(dest));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Update のあと Move した元へ Add しても、移動先の内容は変わらない
    /// </summary>
    /// <remarks>
    /// <para>前提: sub/a.txt を Update し、a.txt へ Move している</para>
    /// <para>手順: sub/a.txt へ AddAsync し、a.txt を ReadAsync して CommitAsync する</para>
    /// <para>期待: 読めるのは Update の内容で、Succeeded のあと a.txt は Update の内容、sub/a.txt は Add の内容である</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateのあとMoveした元へAddしても移動先の内容が変わらないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        string source = System.IO.Path.Combine(work.Path, "sub", "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "init");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("sub/a.txt", updated);
        await tx.MoveAsync("sub/a.txt", "a.txt");
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("sub/a.txt", added);

        Assert.Equal("updated", await tx.ReadAllTextAsync("a.txt"));
        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("updated", await File.ReadAllTextAsync(dest));
        Assert.Equal("added", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Add のあと Move した元へもう一度 Add しても、移動先の内容は変わらない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add し、b.txt へ Move している</para>
    /// <para>手順: a.txt へ AddAsync して CommitAsync する</para>
    /// <para>期待: Succeeded で、b.txt は最初の Add の内容、a.txt は 2 回目の Add の内容である</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddのあとMoveした元へAddしても移動先の内容が変わらないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await tx.MoveAsync("a.txt", "b.txt");
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.AddAsync("a.txt", second);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("first", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// Add を Move で出したパスへ別のファイルを Move して Update しても、先に出した内容は変わらない
    /// </summary>
    /// <remarks>
    /// <para>前提: d.txt を Add して e.txt へ Move し、既存の a.txt を d.txt へ Move している</para>
    /// <para>手順: d.txt へ UpdateAsync して CommitAsync する</para>
    /// <para>期待: Succeeded で、e.txt は Add の内容、d.txt は Update の内容で、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_Addを出したパスへMoveしてUpdateしても先に出した内容が変わらないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream added = LeftoverAddFiles.Utf8Stream("added");
        await tx.AddAsync("d.txt", added);
        await tx.MoveAsync("d.txt", "e.txt");
        await tx.MoveAsync("a.txt", "d.txt");
        await using MemoryStream updated = LeftoverAddFiles.Utf8Stream("updated");
        await tx.UpdateAsync("d.txt", updated);

        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);
        Assert.Equal("added", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "e.txt")));
        Assert.Equal("updated", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "d.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }
}
