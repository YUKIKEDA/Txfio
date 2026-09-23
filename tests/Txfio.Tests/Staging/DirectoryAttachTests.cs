using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DirectoryAttachTests
{
    /// <summary>
    /// 未コミット Dispose ではディレクトリが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを Attach した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: ディレクトリと子ファイルが残る</para>
    /// </remarks>
    [Fact]
    public async Task AttachAsync_未コミットDisposeではディレクトリが残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        string child = System.IO.Path.Combine(dir, "a.txt");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(child, "keep");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.AttachAsync("sub");
        }

        Assert.True(Directory.Exists(dir));
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// 取り込んだディレクトリの配下へ Add できる
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリを Attach している</para>
    /// <para>手順: 配下へ AddAsync する</para>
    /// <para>期待: pending は Attach と Add の 2 件である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_取り込んだディレクトリの配下へ追加できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("sub/a.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 空の取り込みディレクトリを Delete すると直下削除になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 空ディレクトリを Attach している</para>
    /// <para>手順: そのディレクトリを DeleteAsync する</para>
    /// <para>期待: pending は Delete 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_空の取り込みディレクトリはDeleteになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");

        await tx.DeleteAsync("sub");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Delete, pending.Kind);
        Assert.Equal(dir, pending.Path);
    }

    /// <summary>
    /// 子が残る取り込みディレクトリの Delete は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを Attach している</para>
    /// <para>手順: そのディレクトリを DeleteAsync する</para>
    /// <para>期待: ExternalConflictException になり、Attach は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_子がある取り込みディレクトリはExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");

        ExternalConflictException conflict = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.DeleteAsync("sub"));

        Assert.Equal(dir, conflict.Path);
        Assert.Equal(PendingChangeKind.Attach, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 取り込みディレクトリの DeleteTree は全削除になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを Attach している</para>
    /// <para>手順: そのディレクトリを DeleteTreeAsync する</para>
    /// <para>期待: pending は DeleteTree 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_取り込みディレクトリはDeleteTreeになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");

        await tx.DeleteTreeAsync("sub");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.DeleteTree, pending.Kind);
        Assert.Equal(dir, pending.Path);
    }

    /// <summary>
    /// 取り込みディレクトリの Move はディレクトリ Move になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 子ファイルがあるディレクトリを Attach している</para>
    /// <para>手順: そのディレクトリを MoveAsync する</para>
    /// <para>期待: pending は元パスから先への Move 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_取り込みディレクトリはディレクトリMoveになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "sub");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "a.txt"), "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.AttachAsync("sub");

        await tx.MoveAsync("sub", "other");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
        Assert.Equal(dir, pending.Path);
        Assert.Equal(System.IO.Path.Combine(work.Path, "other"), pending.NewPath);
    }
}
