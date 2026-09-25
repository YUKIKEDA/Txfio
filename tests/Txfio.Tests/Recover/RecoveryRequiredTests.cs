using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoveryRequiredTests : IDisposable
{
    public RecoveryRequiredTests()
    {
        CrashInjector.Reset();
    }

    public void Dispose()
    {
        CrashInjector.Reset();
    }

    /// <summary>
    /// 落ちた DeleteTree の残骸があるあいだは、新しいトランザクションを開始できない
    /// </summary>
    /// <remarks>
    /// <para>前提: d/old.txt があり、d の DeleteTree を AfterCommitting で止めて Dispose している</para>
    /// <para>手順: BeginAsync し、RecoverAsync してからもう一度 BeginAsync する</para>
    /// <para>期待: 1 回目は RecoveryRequiredException（Path はワークフォルダ）で d は残る。Recover は RolledForward で d は消え、2 回目は開始できる</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_落ちたDeleteTreeが残っているとRecoveryRequiredExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "old.txt"), "old");
        await CrashDeleteTreeAsync(work.Path, "d");

        RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
            () => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Equal(work.Path, required.Path);
        Assert.True(Directory.Exists(tree));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(tree));

        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }

    /// <summary>
    /// 開始したあとで別のトランザクションが落ちたら、コミットは実体に触れずに拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: d/old.txt がある。tx2 を開始したあと、tx1 が d の DeleteTree を AfterCommitting で止めて Dispose している</para>
    /// <para>手順: tx2 が d/important.txt を Add して CommitAsync し、Dispose してから RecoverAsync する</para>
    /// <para>期待: コミットは RecoveryRequiredException になり、d/important.txt は作られない。Recover は RolledForward で d は消え、ジャーナルは残らない</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_開始後に別トランザクションが落ちるとRecoveryRequiredExceptionで実体に触れないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        string important = System.IO.Path.Combine(tree, "important.txt");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tree, "old.txt"), "old");
        await using (ITransaction tx2 = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await CrashDeleteTreeAsync(work.Path, "d");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("important");
            await tx2.AddAsync(@"d/important.txt", content);

            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(() => tx2.CommitAsync());

            Assert.Equal(work.Path, required.Path);
            Assert.False(File.Exists(important));
            Assert.True(File.Exists(System.IO.Path.Combine(tree, "old.txt")));
        }

        Assert.Empty(Directory.GetFiles(tree, "*.txnew"));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(tree));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// 未コミットの残骸ジャーナルでも開始を拒否し、Recover のあとは開始できる
    /// </summary>
    /// <remarks>
    /// <para>前提: 生存ロックの無い、未コミットのジャーナルだけがある</para>
    /// <para>手順: BeginAsync し、RecoverAsync してからもう一度 BeginAsync する</para>
    /// <para>期待: 1 回目は RecoveryRequiredException で、新しいジャーナルも生存ロックも作らない。Recover は RolledBack で、2 回目は開始できる</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_未コミットの残骸ジャーナルがあるとRecoveryRequiredExceptionになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        await Assert.ThrowsAsync<RecoveryRequiredException>(() => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.lock"));

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        await using ITransaction next = await global::Txfio.Txfio.BeginAsync(work.Path);
        Assert.Empty(next.GetPendingChanges());
    }

    /// <summary>
    /// 操作の無いコミットは残骸ジャーナルを確認しない
    /// </summary>
    /// <remarks>
    /// <para>前提: トランザクションを開始したあと、未コミットの残骸ジャーナルができている</para>
    /// <para>手順: 何も操作せずに CommitAsync する</para>
    /// <para>期待: Succeeded で、残骸ジャーナルは残る</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_操作が無ければ残骸があってもSucceededになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        Guid transactionId = Guid.NewGuid();
        await JournalStore.WriteNewAsync(
            MetadataNames.JournalPath(work.Path, transactionId),
            transactionId,
            CancellationToken.None);

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    private static async Task CrashDeleteTreeAsync(string workFolder, string path)
    {
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        try
        {
            await using ITransaction crashed = await global::Txfio.Txfio.BeginAsync(workFolder);
            await crashed.DeleteTreeAsync(path);
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }
        finally
        {
            CrashInjector.Reset();
        }
    }
}
