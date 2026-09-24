using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class CreateDirectoryTests
{
    /// <summary>
    /// 呼び出した時点で空ディレクトリができ、pending は 1 件である
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダだけがある</para>
    /// <para>手順: CreateDirectoryAsync する</para>
    /// <para>期待: 空ディレクトリがあり、pending は CreateDirectory の 1 件である</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_呼び出した時点で空ディレクトリができること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateDirectoryAsync("drop");

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.CreateDirectory, pending.Kind);
        Assert.Equal(dir, pending.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 未コミットの破棄は、外部が書いた中身ごと消す
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、外部が子ファイルを書いている</para>
    /// <para>手順: Commit せず破棄する</para>
    /// <para>期待: ディレクトリと子ファイルが無い</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_未コミットDisposeでは中身ごと消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        string child = System.IO.Path.Combine(dir, "a.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            await File.WriteAllTextAsync(child, "from-outside");
        }

        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(child));
    }

    /// <summary>
    /// 既にあるパスは作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: drop ディレクトリがある</para>
    /// <para>手順: CreateDirectoryAsync する</para>
    /// <para>期待: ExternalConflictException になり、pending は空である</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_既にあるとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "drop"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateDirectoryAsync("drop"));

        Assert.Contains("作成対象のパスが既に存在します", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 親が無いパスは作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: missing ディレクトリが無い</para>
    /// <para>手順: missing/drop を CreateDirectoryAsync する</para>
    /// <para>期待: ExternalConflictException になり、ディレクトリは無い</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_親が無いとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(
            () => tx.CreateDirectoryAsync("missing/drop"));

        Assert.Contains("親ディレクトリが存在しません", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "missing")));
    }

    /// <summary>
    /// 入れ子の CreateDirectory は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: drop/child を CreateDirectoryAsync する</para>
    /// <para>期待: InvalidOperationException で、pending は drop の 1 件のまま</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateDirectoryAsync("drop/child"));

        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 兄弟ディレクトリは同じトランザクションで作れる
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダだけがある</para>
    /// <para>手順: drop と other を CreateDirectoryAsync する</para>
    /// <para>期待: 両方のディレクトリがあり、pending は 2 件である</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_兄弟は続けて作れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateDirectoryAsync("drop");
        await tx.CreateDirectoryAsync("other");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "other")));
        Assert.Equal(2, tx.GetPendingChanges().Count);
    }

    /// <summary>
    /// 配下への Add は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: drop/a.txt を AddAsync する</para>
    /// <para>期待: InvalidOperationException で、pending は CreateDirectory のまま</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_CreateDirectoryの配下はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("drop/a.txt", content));

        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 木の外への Add は続けられる
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: a.txt を AddAsync する</para>
    /// <para>期待: pending は CreateDirectory と Add の 2 件である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_木の外は続けられること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("yes");

        await tx.AddAsync("a.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(tx.GetPendingChanges(), change => change.Kind == PendingChangeKind.Add);
    }

    /// <summary>
    /// 外部が書いたファイルは ReadAsync で読める
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、外部が a.txt を書いている</para>
    /// <para>手順: ReadAllTextAsync する</para>
    /// <para>期待: 外部が書いた内容が返り、pending は CreateDirectory の 1 件のまま</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_配下のファイルを読めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "outside");

        string text = await tx.ReadAllTextAsync("drop/a.txt");

        Assert.Equal("outside", text);
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 配下のファイルは外へ Export できる
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、外部が a.txt を書いている</para>
    /// <para>手順: ワークフォルダの外へ ExportAsync する</para>
    /// <para>期待: 外に同じ内容があり、pending は CreateDirectory の 1 件のまま</para>
    /// </remarks>
    [Fact]
    public async Task ExportAsync_配下のファイルを外へ出せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "outside");
        string destination = System.IO.Path.Combine(outside.Path, "a.txt");

        await tx.ExportAsync("drop/a.txt", destination);

        Assert.Equal("outside", await File.ReadAllTextAsync(destination));
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// 祖先の全削除は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: parent があり、その中の drop を CreateDirectory している</para>
    /// <para>手順: parent を DeleteTreeAsync する</para>
    /// <para>期待: InvalidOperationException で、drop は残る</para>
    /// </remarks>
    [Fact]
    public async Task DeleteTreeAsync_祖先はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "parent"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("parent/drop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("parent"));

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "parent", "drop")));
    }
}
