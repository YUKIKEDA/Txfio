using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverLivenessTests : IDisposable
{
    public RecoverLivenessTests()
    {
        CrashInjector.Reset();
    }

    public void Dispose()
    {
        CrashInjector.Reset();
    }

    /// <summary>
    /// 生きているトランザクションのジャーナルは Recover で巻き戻さず、そのままコミットできる
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションが Add と CreateDirectory をし、作ったディレクトリへ素のファイル API で書いている</para>
    /// <para>手順: Dispose もコミットもしないまま RecoverAsync し、そのあと同じトランザクションで CommitAsync する</para>
    /// <para>期待: NoPendingTransactions で、ジャーナル、.txnew、ディレクトリの中身が残り、コミットは Succeeded になる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_生きているトランザクションは巻き戻さずコミットできること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string drop = System.IO.Path.Combine(work.Path, "drop");
        string raw = System.IO.Path.Combine(drop, "raw.txt");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
        await tx.AddAsync("a.txt", content);
        await tx.CreateDirectoryAsync("drop");
        await File.WriteAllTextAsync(raw, "raw");

        RecoverResult recovered = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.NoPendingTransactions, recovered);
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Equal("raw", await File.ReadAllTextAsync(raw));

        CommitResult committed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, committed);
        Assert.Equal("staged", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("raw", await File.ReadAllTextAsync(raw));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    /// <summary>
    /// 生きているトランザクションの生存ロックはジャーナルと同じ名前で、終われば消える
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始している</para>
    /// <para>手順: コミット前と、空のまま CommitAsync したあとで、.txfio の tx-*.lock を数える</para>
    /// <para>期待: コミット前はジャーナルと同じ名前の 1 つがあり、コミット後は無い</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_生存ロックはジャーナルと組になりコミットで消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        string journal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        string liveness = Assert.Single(Directory.GetFiles(metadata, "tx-*.lock"));
        Assert.Equal(System.IO.Path.ChangeExtension(journal, ".lock"), liveness);

        Assert.Equal(CommitResult.Succeeded, await tx.CommitAsync());

        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
    }

    /// <summary>
    /// 破棄したトランザクションの生存ロックは消え、Recover するものは無い
    /// </summary>
    /// <remarks>
    /// <para>前提: Add したトランザクションがある</para>
    /// <para>手順: コミットせずに Dispose し、RecoverAsync する</para>
    /// <para>期待: tx-*.lock が無く、NoPendingTransactions になる</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_破棄すると生存ロックが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
        }

        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
        Assert.Equal(RecoverResult.NoPendingTransactions, await global::Txfio.Txfio.RecoverAsync(work.Path));
    }

    /// <summary>
    /// Committing を書いたあとでも、持ち主が生きているあいだは Recover しない
    /// </summary>
    /// <remarks>
    /// <para>前提: Add を AfterCommitting で止め、まだ Dispose していない</para>
    /// <para>手順: RecoverAsync し、Dispose してからもう一度 RecoverAsync する</para>
    /// <para>期待: 1 回目は NoPendingTransactions で対象は無く、2 回目は RolledForward で対象は Add の内容になる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingでも持ち主が生きていれば飛ばすこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using (MemoryStream content = LeftoverAddFiles.Utf8Stream("staged"))
        {
            await tx.AddAsync("a.txt", content);
        }

        await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());

        Assert.Equal(RecoverResult.NoPendingTransactions, await global::Txfio.Txfio.RecoverAsync(work.Path));
        Assert.False(File.Exists(target));

        await tx.DisposeAsync();

        Assert.Equal(RecoverResult.RolledForward, await global::Txfio.Txfio.RecoverAsync(work.Path));
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 生きているトランザクションを飛ばしても、落ちたトランザクションは復旧する
    /// </summary>
    /// <remarks>
    /// <para>前提: AfterCommitting で止めて Dispose したトランザクションと、Add したまま生きているトランザクションがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で止めた Add が反映され、生きている方のジャーナルと生存ロックは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_落ちた方だけを復旧すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        await using (ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("crashed");
            await crashed.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }

        CrashInjector.Reset();
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream liveContent = LeftoverAddFiles.Utf8Stream("live");
        await live.AddAsync("b.txt", liveContent);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("crashed", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.lock"));
        Assert.Equal(CommitResult.Succeeded, await live.CommitAsync());
        Assert.Equal("live", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// 生存ロックの無いジャーナルは、落ちたトランザクションとしてロールバックする
    /// </summary>
    /// <remarks>
    /// <para>前提: 未コミットのジャーナルだけがあり、tx-*.lock は無い（生存ロック導入前の版が残したジャーナル）</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で、ジャーナルも tx-*.lock も残らない</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_生存ロックが無いジャーナルはロールバックすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));
    }
}
