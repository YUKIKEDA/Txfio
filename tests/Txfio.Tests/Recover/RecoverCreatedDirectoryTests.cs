using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverCreatedDirectoryTests
{
    /// <summary>
    /// Recover deletes the destination and .txnew files of a crashed directory copy.
    /// </summary>
    /// <remarks>
    /// <para>Given: src/sub/a.txt exists.</para>
    /// <para>When: CopyAsync to dst, the transaction is discarded without rollback, and RecoverAsync runs.</para>
    /// <para>Then: during the copy, the pending changes are Adds only. RolledBack, dst and the .txnew files are gone, and src remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesDestinationAndTxnewOfCrashedDirectoryCopy()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string nested = System.IO.Path.Combine(source, "sub");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "a.txt"), "hello");

        FaultInjector faults = new FaultInjector();
        faults.SuppressRollback();
        await using (ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await transaction.CopyAsync("src", "dst");
            PendingChange pending = Assert.Single(transaction.GetPendingChanges());
            Assert.Equal(PendingChangeKind.Add, pending.Kind);
        }

        string destination = System.IO.Path.Combine(work.Path, "dst");
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "sub")));
        Assert.NotEmpty(Directory.GetFiles(System.IO.Path.Combine(destination, "sub"), "*.txnew"));

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(nested, "a.txt")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A copy destination without files is also deleted after a crash.
    /// </summary>
    /// <remarks>
    /// <para>Given: an empty src exists.</para>
    /// <para>When: CopyAsync to dst, the transaction is discarded without rollback, and RecoverAsync runs.</para>
    /// <para>Then: there are no pending changes. RolledBack, and dst is gone.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesCrashedEmptyDirectoryCopyDestination()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));

        FaultInjector faults = new FaultInjector();
        faults.SuppressRollback();
        await using (ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await transaction.CopyAsync("src", "dst");
            Assert.Empty(transaction.GetPendingChanges());
        }

        string destination = System.IO.Path.Combine(work.Path, "dst");
        Assert.True(Directory.Exists(destination));
        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(destination));
    }

    /// <summary>
    /// Rollback also deletes the restage backup.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of an uncommitted Add, and its .txnew.prev.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and the .txnew.prev is gone.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollbackDeletesTxnewBackup()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        string backup = leftover.StagingPath + ".prev";
        await File.WriteAllTextAsync(backup, "old");

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(leftover.StagingPath));
    }

    /// <summary>
    /// Recovering a Committing journal does not delete created directories.
    /// </summary>
    /// <remarks>
    /// <para>Given: leftovers of a Committing Add, and a created directory that is only in the journal.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, the Add is finished, and the created directory remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollForwardKeepsCreatedDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string made = System.IO.Path.Combine(work.Path, "made");
        Directory.CreateDirectory(made);
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        json = json.Replace(
            "\"operations\"",
            "\"createdDirectories\":[" + JsonSerializer.Serialize(made) + "],\"operations\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json);

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
        Assert.True(Directory.Exists(made));
    }
}
