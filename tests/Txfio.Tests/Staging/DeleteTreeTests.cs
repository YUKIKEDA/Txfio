using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DeleteTreeTests
{
    /// <summary>
    /// 未コミット Dispose では木が残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを DeleteTree した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: ディレクトリと子ファイルが残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_未コミットDisposeでは木が残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        string child = System.IO.Path.Combine(dir, "a.txt");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(child, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteTreeAsync("tree");
        }

        Assert.True(Directory.Exists(dir));
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// 全削除はジャーナル 1 件で、実体はまだ残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ディレクトリとファイルがある</para>
    /// <para>手順: DeleteTreeAsync する</para>
    /// <para>期待: pending は DeleteTree 1 件で、実体は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_配下があっても1件のDeleteTreeになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string nested = System.IO.Path.Combine(work.Path, "tree", "child");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.DeleteTree, pending.Kind);
        Assert.Equal(System.IO.Path.Combine(work.Path, "tree"), pending.Path);
        Assert.True(File.Exists(System.IO.Path.Combine(nested, "a.txt")));
    }

    /// <summary>
    /// 空ディレクトリも全削除として予約できる
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリがある</para>
    /// <para>手順: DeleteTreeAsync する</para>
    /// <para>期待: pending は DeleteTree 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_空ディレクトリを予約できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ファイルの全削除は未対応
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルがある</para>
    /// <para>手順: そのファイルを DeleteTreeAsync する</para>
    /// <para>期待: UnsupportedOperationException になり、pending は空である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ファイルはUnsupportedOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "file");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.DeleteTreeAsync("a.txt"));
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 無いディレクトリは予約できない
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ディレクトリが無い</para>
    /// <para>手順: DeleteTreeAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は対象である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_無いディレクトリはExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string missing = System.IO.Path.Combine(work.Path, "missing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.DeleteTreeAsync("missing"));
        Assert.Equal(missing, conflict.Path);
    }

    /// <summary>
    /// 配下に操作があると全削除できない
    /// </summary>
    /// <remarks>
    /// <para>前提: 配下ファイルを Delete している</para>
    /// <para>手順: 親を DeleteTreeAsync する</para>
    /// <para>期待: InvalidOperationException になり、先の Delete は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_配下に操作があるとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("tree/a.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("tree"));

        Assert.Equal(PendingChangeKind.Delete, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 全削除のあとの配下操作は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを DeleteTree している</para>
    /// <para>手順: 配下へ Add する</para>
    /// <para>期待: InvalidOperationException になり、DeleteTree は残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_全削除の配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("tree");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("tree/a.txt", content));

        Assert.Equal(PendingChangeKind.DeleteTree, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// ディレクトリ Move の移動先を全削除すると、元ディレクトリの DeleteTree になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 中身があるディレクトリを Move している</para>
    /// <para>手順: 移動先を DeleteTreeAsync する</para>
    /// <para>期待: pending は元ディレクトリの DeleteTree 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ディレクトリMoveの移動先は元のDeleteTreeになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "tree");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("tree", "other");

        await tx.DeleteTreeAsync("other");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.DeleteTree, pending.Kind);
        Assert.Equal(source, pending.Path);
    }

    /// <summary>
    /// ディレクトリ Move の配下は全削除できない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを Move している</para>
    /// <para>手順: 移動元の子ディレクトリを DeleteTreeAsync する</para>
    /// <para>期待: InvalidOperationException になり、Move は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_ディレクトリMoveの配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "tree", "child"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("tree", "other");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("tree/child"));

        Assert.Equal(PendingChangeKind.Move, Assert.Single(tx.GetPendingChanges()).Kind);
    }
}
