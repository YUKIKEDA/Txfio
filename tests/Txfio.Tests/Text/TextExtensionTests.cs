using System.Text;
using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Text;

public sealed class TextExtensionTests
{
    /// <summary>
    /// 無いファイルへの WriteAllText は Add になり、コミットまでディスクに無い
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに a.txt が無い</para>
    /// <para>手順: WriteAllTextAsync してから読む</para>
    /// <para>期待: pending は Add で内容が読め、本物のファイルは無い</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_無いファイルはAddになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// 既存ファイルへの WriteAllText は Update になり、ディスクは古いまま
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の内容は old である</para>
    /// <para>手順: WriteAllTextAsync してコミットする</para>
    /// <para>期待: コミット前のディスクは old、成功後は new、pending は Update</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_既存ファイルはUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "new");

        Assert.Equal(PendingChangeKind.Update, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("new", await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 新規のまま続けて書くと Add のまま内容が置き換わる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt はディスクに無い</para>
    /// <para>手順: WriteAllTextAsync を 2 回する</para>
    /// <para>期待: pending は Add が 1 件で、読めるのは 2 回目の内容</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_新規への再書きはAddのままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "one");
        await tx.WriteAllTextAsync("a.txt", "two");

        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.Equal("two", await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// Delete のあとに書くと Update になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、Delete 済みである</para>
    /// <para>手順: WriteAllTextAsync する</para>
    /// <para>期待: pending は Update が 1 件で、新しい内容が読める</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_DeleteのあとはUpdateになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");

        await tx.WriteAllTextAsync("a.txt", "new");

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Update, pending.Kind);
        Assert.Equal("new", await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// null の文字列は空として Add する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt が無い</para>
    /// <para>手順: contents に null を渡して WriteAllTextAsync する</para>
    /// <para>期待: 空文字が読め、pending は Add</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_nullは空として書くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", null);

        Assert.Equal(string.Empty, await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
    }

    /// <summary>
    /// エンコーディング省略時の書き込みは BOM なし UTF-8 である
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt が無い</para>
    /// <para>手順: エンコーディングを省略して書き、コミットする</para>
    /// <para>期待: 先頭バイトは h で、BOM は無い</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_省略時はBOMなしUTF8であること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello");
        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());

        byte[] bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal((byte)'h', bytes[0]);
        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    /// <summary>
    /// UTF-8 BOM 付きのファイルは、エンコーディングを省略しても文字だけ読める
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt は EF BB BF のあと hello である</para>
    /// <para>手順: ReadAllTextAsync する</para>
    /// <para>期待: hello であり、BOM は文字列に含まれない</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_BOMを検出すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        byte[] payload = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        await File.WriteAllBytesAsync(System.IO.Path.Combine(work.Path, "a.txt"), payload);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string text = await tx.ReadAllTextAsync("a.txt");

        Assert.Equal("hello", text);
    }

    /// <summary>
    /// 指定したエンコーディングで往復でき、BOM があればエンコーディング省略の読みでも同じ文字になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt が無い</para>
    /// <para>手順: UTF-16 で書き、省略した読みと指定した読みの両方をする</para>
    /// <para>期待: どちらも hello で、コミット後の先頭は FF FE</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_指定エンコーディングは往復できること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "hello", Encoding.Unicode);

        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt", Encoding.Unicode));
        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());
        byte[] bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    /// <summary>
    /// 行の書きは末尾改行を付け、読みは改行を含めない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt が無い</para>
    /// <para>手順: 2 行を WriteAllLinesAsync し、行と文字列の両方で読む</para>
    /// <para>期待: 行は a と b、文字列は各行のあとに Environment.NewLine がある</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllLinesAsync_行は改行で区切ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllLinesAsync("a.txt", new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, await tx.ReadAllLinesAsync("a.txt"));
        Assert.Equal("a" + Environment.NewLine + "b" + Environment.NewLine, await tx.ReadAllTextAsync("a.txt"));
    }

    /// <summary>
    /// contents が null なら書かずに ArgumentNullException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: contents に null を渡す</para>
    /// <para>期待: ArgumentNullException になり、pending は空</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllLinesAsync_nullはArgumentNullExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tx.WriteAllLinesAsync("a.txt", null!));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// エンコーディングが null なら書かない
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: encoding に null を渡して WriteAllTextAsync する</para>
    /// <para>期待: ArgumentNullException になり、pending は空</para>
    /// </remarks>
    [Fact]
    public async Task WriteAllTextAsync_エンコーディングnullはArgumentNullExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tx.WriteAllTextAsync("a.txt", "x", null!));

        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 無いファイルとディレクトリは ReadAsync と同じ例外になる
    /// </summary>
    /// <remarks>
    /// <para>前提: missing.txt は無く、dir はディレクトリである</para>
    /// <para>手順: それぞれ ReadAllTextAsync する</para>
    /// <para>期待: 無いファイルは ExternalConflictException、ディレクトリは UnsupportedOperationException</para>
    /// </remarks>
    [Fact]
    public async Task ReadAllTextAsync_無いファイルとディレクトリは読めないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "dir"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.ReadAllTextAsync("missing.txt"));
        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.ReadAllTextAsync("dir"));
    }
}
