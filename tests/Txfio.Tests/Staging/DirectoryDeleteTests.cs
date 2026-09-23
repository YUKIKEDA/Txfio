using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DirectoryDeleteTests
{
    /// <summary>
    /// 未コミット Dispose ではディレクトリが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Delete した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: ディレクトリが残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_未コミットDisposeではディレクトリが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteAsync("sub");
        }

        Assert.True(Directory.Exists(dir));
    }

    /// <summary>
    /// 直下に未追跡のファイルがあると失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリ直下にファイルがある</para>
    /// <para>手順: 親を DeleteAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は親ディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_未追跡の子ファイルがあるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }

    /// <summary>
    /// 直下に未追跡のディレクトリがあると失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリ直下に空の子ディレクトリがある</para>
    /// <para>手順: 親を DeleteAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は親ディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_未追跡の子ディレクトリがあるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(System.IO.Path.Combine(dir, "nested"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }

    /// <summary>
    /// 子ファイルを Delete してから親を Delete できる
    /// </summary>
    /// <remarks>
    /// <para>前提: 直下ファイルを Delete している</para>
    /// <para>手順: 親を DeleteAsync する</para>
    /// <para>期待: pending は Delete 2 件で、実体はまだ残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_子を予約してから親も予約できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        string child = System.IO.Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(child, "gone");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub/a.txt");
        await tx.DeleteAsync("sub");

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(child));
    }

    /// <summary>
    /// Move 出しのあと親を Delete できる
    /// </summary>
    /// <remarks>
    /// <para>前提: 直下ファイルをディレクトリの外へ Move している</para>
    /// <para>手順: 親を DeleteAsync する</para>
    /// <para>期待: 例外にならず、ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_Move出しのあと親を予約できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("sub/a.txt", "b.txt");
        await tx.DeleteAsync("sub");

        Assert.True(Directory.Exists(dir));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 削除予約済みディレクトリへの Add は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Delete している</para>
    /// <para>手順: 直下へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_削除予約済みディレクトリへの追加はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("sub/a.txt", content));
    }

    /// <summary>
    /// 削除するディレクトリへの Move 入りは失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Delete している</para>
    /// <para>手順: 直下へ MoveAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_削除予約済みディレクトリへの移動はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("sub");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", "sub/a.txt"));
    }

    /// <summary>
    /// メタデータフォルダの Delete は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: .txfio を DeleteAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_メタデータフォルダだとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync(".txfio"));
    }

    /// <summary>
    /// メタデータフォルダ配下の Add は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: .txfio 配下へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になり .txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_メタデータフォルダ配下だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync(".txfio/foo", content));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "*.txnew"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// メタデータフォルダ配下の Update / Attach は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: .txfio 配下にファイルがある</para>
    /// <para>手順: UpdateAsync と AttachAsync する</para>
    /// <para>期待: どちらも InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task UpdateとAttach_メタデータフォルダ配下だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, ".txfio"));
        string inside = System.IO.Path.Combine(work.Path, ".txfio", "foo");
        await File.WriteAllTextAsync(inside, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync(".txfio/foo", content));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AttachAsync(".txfio/foo"));
        Assert.Equal("keep", await File.ReadAllTextAsync(inside));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// メタデータフォルダ配下への Move は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダにファイルがある</para>
    /// <para>手順: .txfio 配下へ MoveAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_メタデータフォルダ配下だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("a.txt", ".txfio/a.txt"));
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// メタデータフォルダ配下の Delete は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: .txfio 配下にファイルがある</para>
    /// <para>手順: そのパスを DeleteAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_メタデータフォルダ配下だとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, ".txfio"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, ".txfio", "foo"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteAsync(".txfio/foo"));
    }

    /// <summary>
    /// 残っている Add がある親ディレクトリは Delete できない
    /// </summary>
    /// <remarks>
    /// <para>前提: 直下へ Add している</para>
    /// <para>手順: 親を DeleteAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は親ディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_残るAddがある親はExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("sub/a.txt", content);
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));
        Assert.Equal(dir, ex.Path);
    }
}
