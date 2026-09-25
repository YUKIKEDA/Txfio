using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class ReadTests
{
    /// <summary>
    /// Add の内容は .txnew から読める
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add している。本物のファイルは無い</para>
    /// <para>手順: ReadAsync する</para>
    /// <para>期待: 位置 0 から Add の内容が読め、ロックファイルは 1 つのままである</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_Addの内容を読むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        string lockDirectory = System.IO.Path.GetDirectoryName(PathLockSet.FilePath(work.Path, target))!;
        int locks = Directory.GetFiles(lockDirectory, "*.lock").Length;

        await using Stream stream = await tx.ReadAsync("a.txt");
        Assert.Equal(0, stream.Position);
        Assert.Equal("staged", await ReadTextAsync(stream));
        Assert.False(File.Exists(target));
        Assert.Equal(locks, Directory.GetFiles(lockDirectory, "*.lock").Length);
    }

    /// <summary>
    /// Update は新しい内容を読み、ディスク上の古い内容は残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: ReadAsync する</para>
    /// <para>期待: 読めるのは Update の内容で、本物のファイルは更新前のままである</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_Updateの内容を読むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await tx.UpdateAsync("a.txt", content);

        Assert.Equal("new", await ReadTextAsync(tx, "a.txt"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 未ステージのファイルは本物を読む
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションはファイルをステージしていない</para>
    /// <para>手順: 既存ファイルを ReadAsync する</para>
    /// <para>期待: 本物の内容が読め、ロックフォルダは無い</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_未ステージなら本物を読むこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        Assert.Equal("disk", await ReadTextAsync(tx, "a.txt"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, ".txfio", "locks")));
    }

    /// <summary>
    /// Delete 予約中のファイルは無い
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Delete 予約している</para>
    /// <para>手順: ReadAsync する</para>
    /// <para>期待: ExternalConflictException になり、ディスク上のファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_Delete予約中はExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("a.txt"));
        Assert.Equal(target, missing.Path);
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Move の移動先は移動元のバイトを読み、移動元は無い
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を b.txt へ Move 予約している</para>
    /// <para>手順: 移動元と移動先を ReadAsync する</para>
    /// <para>期待: 移動先は移動元の内容が読め、移動元は ExternalConflictException になり Path は a.txt の絶対パスである</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_Moveの移動先は元のバイトで移動元はExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        Assert.Equal("src", await ReadTextAsync(tx, "b.txt"));
        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("a.txt"));
        Assert.Equal(source, missing.Path);
        Assert.Equal("src", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// ファイルが無いパスは ExternalConflictException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象ファイルが無い</para>
    /// <para>手順: ReadAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は対象の絶対パスである</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ファイルが無いとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "missing.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException missing = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAsync("missing.txt"));
        Assert.Equal(target, missing.Path);
    }

    /// <summary>
    /// ディレクトリは読めない
    /// </summary>
    /// <remarks>
    /// <para>前提: ディレクトリがある</para>
    /// <para>手順: そのディレクトリを ReadAsync する</para>
    /// <para>期待: UnsupportedOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_ディレクトリはUnsupportedOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAsync("sub"));
    }

    /// <summary>
    /// メタデータ配下とワークフォルダの外は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: .txfio 配下と、別フォルダの絶対パスを ReadAsync する</para>
    /// <para>期待: 前者は InvalidOperationException、後者は ArgumentException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_メタデータ配下とワークフォルダの外は拒否すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory other = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ReadAsync(".txfio/foo"));
        string outside = System.IO.Path.Combine(other.Path, "a.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => tx.ReadAsync(outside));
    }

    /// <summary>
    /// コミット済みは読めない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のトランザクションをコミットしている</para>
    /// <para>手順: ReadAsync する</para>
    /// <para>期待: InvalidOperationException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_コミット済みだとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.ReadAsync("a.txt"));
    }

    /// <summary>
    /// 読み取り中でもコミットできる
    /// </summary>
    /// <remarks>
    /// <para>前提: Add した内容を ReadAsync で開いたままにしている</para>
    /// <para>手順: CommitAsync し、開いたストリームを読む</para>
    /// <para>期待: Succeeded で対象ファイルができ、ストリームからも Add の内容が読める</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_開いたままでもコミットできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await using Stream stream = await tx.ReadAsync("a.txt");

        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Equal("staged", await ReadTextAsync(stream));
    }

    /// <summary>
    /// 読み取り中の再ステージは失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: Add した内容を ReadAsync で開いたままにしている</para>
    /// <para>手順: 同じパスを Update する</para>
    /// <para>期待: IOException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_開いたまま再ステージするとIOExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await using Stream stream = await tx.ReadAsync("a.txt");
        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("next");

        await Assert.ThrowsAsync<IOException>(() => tx.UpdateAsync("a.txt", again));
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// 開始時に取り消されていれば読まない
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルがある</para>
    /// <para>手順: 取り消されたトークンで ReadAsync する</para>
    /// <para>期待: OperationCanceledException になる</para>
    /// </remarks>
    [Fact]
    public async Task ReadAsync_開始時に取り消されているとOperationCanceledExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "disk");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        using CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tx.ReadAsync("a.txt", source.Token));
    }

    private static async Task<string> ReadTextAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        return await ReadTextAsync(stream);
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
