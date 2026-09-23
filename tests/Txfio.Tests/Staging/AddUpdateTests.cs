using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class AddUpdateTests
{
    /// <summary>
    /// Add すると対象と同じ場所に .txnew ができ、対象パスはまだ無い
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダがある</para>
    /// <para>手順: AddAsync する</para>
    /// <para>期待: pending は Add 1 件で、.txnew があり対象ファイルは無い</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_コミット前は対象パスを作らずtxnewだけがあること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
        await tx.AddAsync("a.txt", content);

        string target = System.IO.Path.Combine(work.Path, "a.txt");
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        Assert.Equal(target, pending[0].Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 未コミット Dispose は .txnew を消し、対象パスは作らない
    /// </summary>
    /// <remarks>
    /// <para>前提: Add した直後である</para>
    /// <para>手順: Commit せず Dispose する</para>
    /// <para>期待: 対象も .txnew も残らない</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_未コミットDisposeでtxnewが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
            await tx.AddAsync("a.txt", content);
        }

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 既存ファイルへの Add はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスにファイルがある</para>
    /// <para>手順: AddAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は対象で、.txnew は無い</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_既存ファイルだとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "existing");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AddAsync("a.txt", content));
        Assert.Equal(target, ex.Path);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 無いファイルへの Update はその場で失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 対象パスにファイルが無い</para>
    /// <para>手順: UpdateAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は対象である</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_無いファイルだとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.UpdateAsync("missing.txt", content));
        Assert.Equal(System.IO.Path.Combine(work.Path, "missing.txt"), ex.Path);
    }

    /// <summary>
    /// 親ディレクトリが無いパスは自動作成しない
    /// </summary>
    /// <remarks>
    /// <para>前提: サブフォルダが無い</para>
    /// <para>手順: その配下へ AddAsync する</para>
    /// <para>期待: ExternalConflictException になり、Path は親ディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_親ディレクトリが無いとExternalConflictExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        ExternalConflictException ex = await Assert.ThrowsAsync<ExternalConflictException>(() => tx.AddAsync("sub\\a.txt", content));
        Assert.Equal(System.IO.Path.Combine(work.Path, "sub"), ex.Path);
    }

    /// <summary>
    /// ワークフォルダの外は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダの外にパスがある</para>
    /// <para>手順: その絶対パスへ AddAsync する</para>
    /// <para>期待: ArgumentException になる</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ワークフォルダの外だとArgumentExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory other = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        string outside = System.IO.Path.Combine(other.Path, "a.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => tx.AddAsync(outside, content));
    }

    /// <summary>
    /// 同一パスへの再 Add は .txnew を上書きし、pending は 1 件のまま
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを既に Add している</para>
    /// <para>手順: 別内容で再度 AddAsync する</para>
    /// <para>期待: pending は 1 件で、.txnew の内容は後者</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_同一パスの再ステージは上書きして1件のままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.AddAsync("a.txt", second);

        Assert.Single(tx.GetPendingChanges());
        string[] sidecars = Directory.GetFiles(work.Path, "*.txnew");
        Assert.Single(sidecars);
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
    }

    /// <summary>
    /// Add したパスへの Update は Add のまま内容だけ入れ替える
    /// </summary>
    /// <remarks>
    /// <para>前提: 同じパスを Add している（対象パスはまだ無い）</para>
    /// <para>手順: UpdateAsync する</para>
    /// <para>期待: pending の種類は Add のままで、.txnew は新しい内容</para>
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_未コミットのAddに対してはAddのまま上書きすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
        await tx.AddAsync("a.txt", first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");
        await tx.UpdateAsync("a.txt", second);

        IReadOnlyList<PendingChange> pending = tx.GetPendingChanges();
        Assert.Single(pending);
        Assert.Equal(PendingChangeKind.Add, pending[0].Kind);
        string[] sidecars = Directory.GetFiles(work.Path, "*.txnew");
        Assert.Equal("second", await File.ReadAllTextAsync(sidecars[0]));
    }

    /// <summary>
    /// 呼び出し側の Stream は Dispose しない
    /// </summary>
    /// <remarks>
    /// <para>前提: MemoryStream を渡す</para>
    /// <para>手順: AddAsync する</para>
    /// <para>期待: 呼び出し後も Stream を読める</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_呼び出し側のStreamをDisposeしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");
        await tx.AddAsync("a.txt", content);
        Assert.True(content.CanRead);
        content.Dispose();
    }

    /// <summary>
    /// 同一パスの再ステージで journal 書き込みに失敗しても .txnew は元の内容のまま
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したあと、journal を排他ロックしている</para>
    /// <para>手順: 同じパスへ再度 AddAsync する</para>
    /// <para>期待: IOException になり、pending は Add 1 件で、.txnew の内容は前者</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_再ステージでjournal書き込みに失敗するとtxnewは元の内容のままであること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string workPath = work.Path;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(workPath))
        {
            await using MemoryStream first = LeftoverAddFiles.Utf8Stream("first");
            await tx.AddAsync("a.txt", first);
            await using FileStream journalLock = LockJournal(workPath);
            await using MemoryStream second = LeftoverAddFiles.Utf8Stream("second");

            IOException ex = await Assert.ThrowsAsync<IOException>(() => tx.AddAsync("a.txt", second));
            Assert.Null(ex.InnerException);

            PendingChange pending = Assert.Single(tx.GetPendingChanges());
            Assert.Equal(PendingChangeKind.Add, pending.Kind);
            string[] sidecars = Directory.GetFiles(workPath, "*.txnew");
            Assert.Single(sidecars);
            Assert.Equal("first", await File.ReadAllTextAsync(sidecars[0]));
            Assert.Empty(Directory.GetFiles(workPath, "*.prev"));
        }

        Assert.Empty(Directory.GetFiles(workPath, "*.txnew"));
        Assert.Empty(Directory.GetFiles(workPath, "*.prev"));
    }

    private static FileStream LockJournal(string workFolder)
    {
        string journal = Assert.Single(
            Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal"));
        return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
