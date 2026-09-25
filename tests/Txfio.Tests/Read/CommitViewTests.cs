using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class CommitViewTests
{
    /// <summary>
    /// ファイル Move の移動元へ Add した内容が読める
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を a.bak へ Move したあと、a.txt へ Add している</para>
    /// <para>手順: a.txt と a.bak を ReadAsync し、ExistsAsync する</para>
    /// <para>期待: a.txt は新しい内容、a.bak は旧内容で、どちらも存在する</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_移動元へのAddはその内容が読めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.AddAsync("a.txt", content);

        Assert.Equal("new", await ReadTextAsync(tx, "a.txt"));
        Assert.Equal("old", await ReadTextAsync(tx, "a.bak"));
        Assert.True(await tx.ExistsAsync("a.txt"));
        Assert.True(await tx.ExistsAsync("a.bak"));
    }

    /// <summary>
    /// 連鎖の途中のパスは、空いている端から適用したあとのバイトを読む
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2)、Move(log→log.1)、log.txt へ Add し、3 つを読む</para>
    /// <para>期待: log.2 は旧 log.1、log.1 は旧 log、log.txt は新しい内容</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_連鎖は空いている端からのバイトを読むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("fresh");
        await tx.AddAsync("log.txt", content);

        Assert.Equal("older", await ReadTextAsync(tx, "log.2"));
        Assert.Equal("current", await ReadTextAsync(tx, "log.1"));
        Assert.Equal("fresh", await ReadTextAsync(tx, "log.txt"));
        Assert.False(await tx.ExistsAsync("missing.txt"));
    }

    /// <summary>
    /// DeleteTree の配下は無い
    /// </summary>
    /// <remarks>
    /// <para>前提: dir/a.txt があり、dir を DeleteTree している</para>
    /// <para>手順: dir と dir/a.txt を ExistsAsync し、dir/a.txt を ReadAsync する</para>
    /// <para>期待: どちらも無く、読み取りは ExternalConflictException になり、ディスク上のファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_DeleteTreeの配下は無いこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "dir");
        string child = System.IO.Path.Combine(dir, "a.txt");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(child, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("dir");

        Assert.False(await tx.ExistsAsync("dir"));
        Assert.False(await tx.ExistsAsync("dir/a.txt"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("dir/a.txt"));
        Assert.Equal("keep", await File.ReadAllTextAsync(child));
    }

    /// <summary>
    /// ディレクトリ Move の移動先の配下は移動元のファイルを読み、移動元の配下は無い
    /// </summary>
    /// <remarks>
    /// <para>前提: old/a.txt と mid/b.txt があり、Move(mid→next) のあと Move(old→mid) している</para>
    /// <para>手順: 各パスを ReadAsync または ExistsAsync する</para>
    /// <para>期待: next/b.txt は mid の内容、mid/a.txt は old の内容、old/a.txt は無く、移動先のディレクトリ mid と next は true</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ディレクトリMoveの配下は移動元に対応すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string oldDir = System.IO.Path.Combine(work.Path, "old");
        string midDir = System.IO.Path.Combine(work.Path, "mid");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(midDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldDir, "a.txt"), "from-old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(midDir, "b.txt"), "from-mid");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        Assert.Equal("from-mid", await ReadTextAsync(tx, "next/b.txt"));
        Assert.Equal("from-old", await ReadTextAsync(tx, "mid/a.txt"));
        Assert.False(await tx.ExistsAsync("old/a.txt"));
        Assert.False(await tx.ExistsAsync("old"));
        Assert.True(await tx.ExistsAsync("mid"));
        Assert.True(await tx.ExistsAsync("next"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("next"));
    }

    /// <summary>
    /// 無いパスの ExistsAsync は false で、ディレクトリは true
    /// </summary>
    /// <remarks>
    /// <para>前提: sub があり、missing.txt は無い</para>
    /// <para>手順: ExistsAsync する</para>
    /// <para>期待: sub は true、missing.txt は false で、sub の ReadAsync は UnsupportedOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task ExistsAsync_ディレクトリはtrueで無いパスはfalseになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        Assert.True(await tx.ExistsAsync("sub"));
        Assert.False(await tx.ExistsAsync("missing.txt"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("sub"));
    }

    /// <summary>
    /// メタデータ配下とワークフォルダの外の ExistsAsync は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: .txfio 配下と、別フォルダの絶対パスを ExistsAsync する</para>
    /// <para>期待: 前者は InvalidOperationException、後者は ArgumentException になる</para>
    /// </remarks>
    [Fact]
    public async Task ExistsAsync_メタデータ配下とワークフォルダの外は拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ExistsAsync(".txfio/foo"));
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ExistsAsync(outside.Path));
    }

    private static async Task<string> ReadTextAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        using StreamReader reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
