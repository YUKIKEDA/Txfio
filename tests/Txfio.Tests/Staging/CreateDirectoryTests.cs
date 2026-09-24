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
    /// 未コミットの破棄は、素のファイル API で書いた中身ごと消す
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、素のファイル API で子ファイルを書いている</para>
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
    /// 入れ子の CreateDirectory は親と同じ規則で作れる
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: drop/child を CreateDirectoryAsync する</para>
    /// <para>期待: 両方のディレクトリがあり、pending は CreateDirectory の 2 件である</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_配下も作れること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        await tx.CreateDirectoryAsync("drop/child");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop", "child")));
        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.All(tx.GetPendingChanges(), change => Assert.Equal(PendingChangeKind.CreateDirectory, change.Kind));
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
    /// 配下への Add は pending に出る。素のファイル API で書いたファイルは出ない
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: drop/a.txt を AddAsync し、drop/raw.txt を素のファイル API で書く</para>
    /// <para>期待: pending は CreateDirectory と Add の 2 件である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_配下は予約として見えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("yes");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "raw.txt"), "raw");

        await tx.AddAsync("drop/a.txt", content);

        Assert.Equal(2, tx.GetPendingChanges().Count);
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            tx.GetPendingChanges(),
            change => change.Path.EndsWith("raw.txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 作ったディレクトリ自身への変更系は畳まない
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory している</para>
    /// <para>手順: そのパスへ Delete、DeleteTree、Move の元と先、Attach、Update、もう一度の CreateDirectory を呼ぶ</para>
    /// <para>期待: どれも InvalidOperationException で、pending は 1 件のまま</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_そのパス自身の変更系はInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string drop = System.IO.Path.Combine(work.Path, "drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "src.txt"), "src");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        InvalidOperationException delete = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tx.DeleteAsync("drop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.DeleteTreeAsync("drop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("drop", "other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.MoveAsync("src.txt", "drop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.AttachAsync("drop"));
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("no");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.UpdateAsync("drop", content));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CreateDirectoryAsync("drop"));

        Assert.Contains("このパスは既に別の操作でステージングされています", delete.Message, StringComparison.Ordinal);
        Assert.Equal(PendingChangeKind.CreateDirectory, Assert.Single(tx.GetPendingChanges()).Kind);
        Assert.True(Directory.Exists(drop));
        Assert.True(File.Exists(System.IO.Path.Combine(work.Path, "src.txt")));
    }

    /// <summary>
    /// 配下に操作が無いディレクトリはコピー元にできる
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory し、素のファイル API で drop/a.txt を書いている</para>
    /// <para>手順: drop を copy へ CopyAsync する</para>
    /// <para>期待: copy/a.txt が Add になり、drop は残る</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_配下に操作が無ければコピー元にできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "drop", "a.txt"), "raw");

        await tx.CopyAsync("drop", "copy");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith(System.IO.Path.Combine("copy", "a.txt"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 配下に操作があると、そのディレクトリはコピー元にできない
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory し、drop/a.txt を Add している</para>
    /// <para>手順: drop を copy へ CopyAsync する</para>
    /// <para>期待: InvalidOperationException で、copy は無い</para>
    /// </remarks>
    [Fact]
    public async Task CopyAsync_配下に操作があるとInvalidOperationExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("drop/a.txt", content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CopyAsync("drop", "copy"));

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "copy")));
    }

    /// <summary>
    /// 外のディレクトリは、作ったディレクトリの配下へ Import できる
    /// </summary>
    /// <remarks>
    /// <para>前提: drop を CreateDirectory し、ワークフォルダの外に src/a.txt がある</para>
    /// <para>手順: src を drop/in へ ImportAsync する</para>
    /// <para>期待: drop/in/a.txt が Add になり、外の src は残る</para>
    /// </remarks>
    [Fact]
    public async Task ImportAsync_配下へディレクトリを取り込めること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string source = System.IO.Path.Combine(outside.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "in");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");

        await tx.ImportAsync(source, "drop/in");

        Assert.True(File.Exists(System.IO.Path.Combine(source, "a.txt")));
        Assert.Contains(
            tx.GetPendingChanges(),
            change => change.Kind == PendingChangeKind.Add
                && change.Path.EndsWith(System.IO.Path.Combine("drop", "in", "a.txt"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 破棄は、配下へ Add したサイドカーと Attach したファイルも消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 開始前のファイルを drop へ動かして Attach し、drop/new.txt を Add している</para>
    /// <para>手順: Commit せず破棄する</para>
    /// <para>期待: drop も、動かしたファイルも、Add も無い</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_配下のAddとAttachも消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string moved = System.IO.Path.Combine(work.Path, "drop", "keep.txt");
        string added = System.IO.Path.Combine(work.Path, "drop", "new.txt");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "keep.txt"), "old");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("drop");
            File.Move(System.IO.Path.Combine(work.Path, "keep.txt"), moved);
            await tx.AttachAsync("drop/keep.txt");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.AddAsync("drop/new.txt", content);
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        Assert.False(File.Exists(moved));
        Assert.False(File.Exists(added));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "keep.txt")));
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
    /// 素のファイル API で書いたファイルは ReadAsync で読める
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory のあと、素のファイル API で a.txt を書いている</para>
    /// <para>手順: ReadAllTextAsync する</para>
    /// <para>期待: 書いた内容が返り、pending は CreateDirectory の 1 件のまま</para>
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
    /// <para>前提: CreateDirectory のあと、素のファイル API で a.txt を書いている</para>
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
