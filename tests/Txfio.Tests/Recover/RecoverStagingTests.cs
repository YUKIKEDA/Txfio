using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverStagingTests
{
    /// <summary>
    /// Recover of uncommitted leftovers deletes the .txnew and does not create the target.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and an Add journal and its .txnew remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the .txnew and the journal are deleted, and the target does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesUncommittedTxnewAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.False(File.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// Recover of Committing leftovers moves the .txnew to the target.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a Committing journal and its .txnew remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, the target has the content, and there is no .txnew and no journal.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_MovesCommittingTxnewAndRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
    }

    /// <summary>
    /// If applying a Committing journal fails, ConflictDetected is returned once, and the journal and .txnew are deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of a Committing Add, and the target path has already been created externally.</para>
    /// <para>When: RecoverAsync is called twice.</para>
    /// <para>Then: the first is ConflictDetected, the journal and .txnew are gone, and the target keeps the external content. The second is NoPendingTransactions.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FailedCommittingApplyIsConflictDetectedAndDeletesJournal()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.TargetPath, "external");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.StagingPath));
        Assert.Equal("external", await File.ReadAllTextAsync(leftover.TargetPath));
        Assert.Equal(RecoverResult.NoPendingTransactions, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
    }
}
