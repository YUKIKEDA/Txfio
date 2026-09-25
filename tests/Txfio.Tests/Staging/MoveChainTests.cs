using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class MoveChainTests
{
    /// <summary>
    /// 空いている端から呼ぶと、別ファイルの Move は 2 件のまま残る
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2) のあと Move(log→log.1) する</para>
    /// <para>期待: pending は 2 件の Move で、ディスク上のファイルはまだ動いていない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_別ファイルの連鎖は畳まずに残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.txt"), "current");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "log.1"), "older");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("log.1", "log.2");
        await tx.MoveAsync("log.txt", "log.1");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.EndsWith("log.1", pending[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("log.2", pending[0].NewPath, StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.Move, pending[1].Kind);
        Assert.EndsWith("log.txt", pending[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("log.1", pending[1].NewPath, StringComparison.Ordinal);
        Assert.Equal("older", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "log.1")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "log.2")));
    }

    /// <summary>
    /// ファイル Move の移動元へは、ディスク上にファイルがあっても Add できる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を a.bak へ Move している</para>
    /// <para>手順: a.txt へ AddAsync する</para>
    /// <para>期待: pending は Move と Add で、a.txt の旧内容はディスクに残る</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ファイルMoveの移動元へ書けること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "a.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.AddAsync("a.txt", content);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Add, pending[1].Kind);
        Assert.Equal(source, pending[1].Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
    }

    /// <summary>
    /// ディレクトリ Move の移動元への Add は失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: dir を dir.bak へ Move している</para>
    /// <para>手順: dir へ AddAsync する</para>
    /// <para>期待: InvalidOperationException で、pending は Move のまま</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ディレクトリMoveの移動元は失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "dir"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("dir", "dir.bak");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AddAsync("dir", content));

        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.Equal(PendingChangeKind.Move, pending.Kind);
    }

    /// <summary>
    /// 移動先が存在し、別の Move の移動元でもないときは失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt がある</para>
    /// <para>手順: Move(a→b) する</para>
    /// <para>期待: ExternalConflictException で、Path は移動先、pending は空</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_移動先が別のMoveの移動元でなければ失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "src");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(dest, "dst");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.MoveAsync("a.txt", "b.txt"));

        Assert.Equal(dest, ex.Path);
        Assert.Empty(tx.GetPendingChanges());
    }

    /// <summary>
    /// 互いに移動先になる Move は循環として受け付けない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt があり、b.txt は無い</para>
    /// <para>手順: Move(a→b) のあと Move(b→a) する</para>
    /// <para>期待: 2 件目は InvalidOperationException で、pending は 1 件のまま、ファイルは動いていない</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_循環は受け付けないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(source, "keep");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("b.txt", "a.txt"));

        Assert.Contains("空いている端が無い移動は受け付けられません", ex.Message, StringComparison.Ordinal);
        PendingChange pending = Assert.Single(tx.GetPendingChanges());
        Assert.EndsWith("b.txt", pending.NewPath, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 一時名を挟んだ入れ替えは、畳み込みが循環になるので 3 件目で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、tmp は無い</para>
    /// <para>手順: Move(a→tmp)、Move(b→a)、Move(tmp→b) の順に呼ぶ</para>
    /// <para>期待: 3 件目は InvalidOperationException で、pending は先の 2 件の Move のまま</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_一時名の入れ替えは3件目で失敗すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "b");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "tmp");
        await tx.MoveAsync("b.txt", "a.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("tmp", "b.txt"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.EndsWith("tmp", pending[0].NewPath, StringComparison.Ordinal);
        Assert.EndsWith("a.txt", pending[1].NewPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// ディレクトリも、空いている端からなら連鎖を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: old と mid があり、next は無い</para>
    /// <para>手順: Move(mid→next) のあと Move(old→mid) する</para>
    /// <para>期待: pending は 2 件のディレクトリ Move である</para>
    /// </remarks>
    [Fact]
    public async Task MoveAsync_ディレクトリの連鎖も畳まずに残ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "old"));
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "mid"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.MoveAsync("mid", "next");
        await tx.MoveAsync("old", "mid");

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        Assert.Equal(PendingChangeKind.Move, pending[0].Kind);
        Assert.Equal(PendingChangeKind.Move, pending[1].Kind);
        Assert.EndsWith("next", pending[0].NewPath, StringComparison.Ordinal);
        Assert.EndsWith("mid", pending[1].NewPath, StringComparison.Ordinal);
    }
}
