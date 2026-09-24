using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitApplyExceptionTests : IDisposable
{
    public CommitApplyExceptionTests()
    {
        StagingApplier.ClearApplyFailure();
    }

    public void Dispose()
    {
        StagingApplier.ClearApplyFailure();
    }

    /// <summary>
    /// Committing を書いたあとの例外では、Dispose がロールバックせず Recover が Add を確定する
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory と、その配下への Add をステージングしている</para>
    /// <para>手順: 適用の最初で FileNotFoundException を出して CommitAsync し、Dispose してから RecoverAsync する</para>
    /// <para>期待: 例外はそのまま届く。ディレクトリと .txnew とジャーナルは残る。Recover のあと RolledForward で、対象は Add の内容になる</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_適用中の例外ではDisposeがロールバックせずRecoverがAddを確定すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        string target = System.IO.Path.Combine(tree, "a.txt");
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.CreateDirectoryAsync("d");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync(@"d/a.txt", content);
            StagingApplier.FailNextApply(new FileNotFoundException("missing"));

            await Assert.ThrowsAsync<FileNotFoundException>(() => tx.CommitAsync());
        }

        Assert.True(Directory.Exists(tree));
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(tree, "*.txnew"));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        await Assert.ThrowsAsync<RecoveryRequiredException>(() => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Equal(RecoverResult.RolledForward, await global::Txfio.Txfio.RecoverAsync(work.Path));
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(tree, "*.txnew"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }
}
