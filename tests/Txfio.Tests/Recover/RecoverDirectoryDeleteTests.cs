using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDirectoryDeleteTests
{
    /// <summary>
    /// Recover of an uncommitted directory Delete keeps the target.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a directory Delete journal and an empty directory remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the journal is deleted, and the directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UncommittedDirectoryDeleteKeepsTargetAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: false,
            "sub");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.True(Directory.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// Recover finishes a Committing Delete of an empty directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing directory Delete journal and an empty directory.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and neither the directory nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesCommittingEmptyDirectoryDeleteAndRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: true,
            "sub");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(Directory.Exists(leftover.TargetPath));
    }

    /// <summary>
    /// With Committing, if the directory has direct children, it keeps the directory and deletes the journal.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing directory Delete journal exists, and a file is directly under the directory.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: ConflictDetected, the directory remains, and the journal is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingDirectoryWithChildIsConflictDetected()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverDirectoryDeleteFiles leftover = await LeftoverDirectoryDeleteFiles.WriteDirectoryDeleteAsync(
            work.Path,
            committing: true,
            "sub");
        await File.WriteAllTextAsync(System.IO.Path.Combine(leftover.TargetPath, "external.txt"), "no");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.True(Directory.Exists(leftover.TargetPath));
    }
}
