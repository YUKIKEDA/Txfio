using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitApplyExceptionTests
{
    /// <summary>
    /// On an exception after Committing is written, Dispose does not roll back, and Recover finishes the Add.
    /// </summary>
    /// <remarks>
    /// <para>Given: a CreateDirectory and an Add under it are staged.</para>
    /// <para>When: CommitAsync throws FileNotFoundException at the start of apply, then the transaction is disposed and RecoverAsync runs.</para>
    /// <para>Then: the exception reaches the caller as is; the directory, the .txnew, and the journal remain; after Recover the result is RolledForward and the target has the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ExceptionDuringApplyKeepsStateAndRecoverFinishesAdd()
    {
        await using TempDirectory work = TempDirectory.Create();
        string tree = System.IO.Path.Combine(work.Path, "d");
        string target = System.IO.Path.Combine(tree, "a.txt");
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.CreateDirectoryAsync("d");
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync(@"d/a.txt", content);
            faults.FailNextApply(new FileNotFoundException("missing"));

            await Assert.ThrowsAsync<FileNotFoundException>(() => tx.CommitAsync());
        }

        Assert.True(Directory.Exists(tree));
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(tree, "*.txnew"));
        Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        await Assert.ThrowsAsync<RecoveryRequiredException>(() => global::Txfio.Txfio.BeginAsync(work.Path));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(tree, "*.txnew"));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
    }
}
