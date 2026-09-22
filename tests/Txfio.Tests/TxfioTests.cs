using Txfio.Tests.Support;

namespace Txfio.Tests;

public sealed class TxfioTests
{
    /// <summary>
    /// ライブラリアセンブリの名前は Txfio である
    /// </summary>
    /// <remarks>
    /// <para>前提: テストが Txfio を参照している</para>
    /// <para>手順: 公開型 Txfio のアセンブリ名を読む</para>
    /// <para>期待: 名前が Txfio である</para>
    /// </remarks>
    [Fact]
    public void アセンブリ名がTxfioであること()
    {
        Assert.Equal("Txfio", typeof(global::Txfio.Txfio).Assembly.GetName().Name);
    }

    /// <summary>
    /// 存在しないワークフォルダではトランザクションを開始できない
    /// </summary>
    /// <remarks>
    /// <para>前提: パスにディレクトリが無い</para>
    /// <para>手順: BeginAsync を呼ぶ</para>
    /// <para>期待: DirectoryNotFoundException になる</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_存在しないフォルダだとDirectoryNotFoundExceptionになること()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "txfio-missing-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => global::Txfio.Txfio.BeginAsync(missing));
    }

    /// <summary>
    /// 開始するとジャーナルができ、未コミット Dispose で消える
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダがある</para>
    /// <para>手順: BeginAsync したあと Commit せず Dispose する</para>
    /// <para>期待: 開始直後は tx-*.journal があり、Dispose 後は無い</para>
    /// </remarks>
    [Fact]
    public async Task BeginAsync_未コミットDisposeでジャーナルが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string journal;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            string[] files = Directory.GetFiles(
                System.IO.Path.Combine(work.Path, ".txfio"),
                "tx-*.journal");
            Assert.Single(files);
            journal = files[0];
            Assert.True(File.Exists(journal));
            Assert.Empty(tx.GetPendingChanges());
        }

        Assert.False(File.Exists(journal));
    }

    /// <summary>
    /// 空のコミットは成功し、ジャーナルを削除する
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダでトランザクションを開始している</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded で、ジャーナルが残らない</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_空のトランザクションはSucceededでジャーナルが消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        CommitResult result = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, result);

        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }

    /// <summary>
    /// 未完了ジャーナルが無ければ Recover は何もしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 空のワークフォルダがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: NoPendingTransactions</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ジャーナルが無いとNoPendingTransactionsになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.NoPendingTransactions, result);
    }

    /// <summary>
    /// Committing でない残骸ジャーナルは Recover が削除する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダに未コミットの journal が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack でファイルが消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットジャーナルを削除してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        string[] files = Directory.GetFiles(
            System.IO.Path.Combine(work.Path, ".txfio"),
            "tx-*.journal");
        Assert.Single(files);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.False(File.Exists(files[0]));

        await tx.DisposeAsync();
    }
}
