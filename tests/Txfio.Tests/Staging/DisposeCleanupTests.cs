using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DisposeCleanupTests
{
    /// <summary>
    /// Even if cleanup fails partway, the original exception reaches the caller.
    /// </summary>
    /// <remarks>
    /// <para>Given: two Adds and an empty CreateDirectory, and the first .txnew is open without sharing.</para>
    /// <para>When: an InvalidOperationException is thrown in that state and the transaction is disposed.</para>
    /// <para>Then: the exception that arrives is InvalidOperationException, the later .txnew and the empty directory are gone, and the open .txnew and the journal remain.</para>
    /// </remarks>
    [WindowsFact("An open file cannot be deleted")]
    public async Task DisposeAsync_OriginalExceptionArrivesWhenCleanupFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        FileStream? hold = null;
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
                await using MemoryStream first = LeftoverAddFiles.Utf8Stream("a");
                await transaction.AddAsync("a.txt", first);
                await transaction.CreateDirectoryAsync("empty");
                await using MemoryStream second = LeftoverAddFiles.Utf8Stream("b");
                await transaction.AddAsync("b.txt", second);
                string locked = Assert.Single(
                    Directory.GetFiles(work.Path, "*.txnew"),
                    path => System.IO.Path.GetFileName(path).StartsWith("a.txt.", StringComparison.OrdinalIgnoreCase));
                hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
                throw new InvalidOperationException("original failure");
            });

            Assert.Equal("original failure", error.Message);
            Assert.NotNull(hold);
            Assert.True(File.Exists(hold.Name));
            Assert.DoesNotContain(
                Directory.GetFiles(work.Path, "*.txnew"),
                path => System.IO.Path.GetFileName(path).StartsWith("b.txt.", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "empty")));
            Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
                () => global::Txfio.Txfio.BeginAsync(work.Path));
            Assert.Equal(work.Path, required.Path, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (hold is not null)
            {
                await hold.DisposeAsync();
            }
        }

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// When cleanup succeeds, the original exception arrives and the journal is deleted.
    /// </summary>
    /// <remarks>
    /// <para>Given: one Add.</para>
    /// <para>When: an InvalidOperationException is thrown and the transaction is disposed.</para>
    /// <para>Then: the exception that arrives is InvalidOperationException, and there is no .txnew and no journal.</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_OriginalExceptionArrivesAndJournalIsDeletedWhenCleanupSucceeds()
    {
        await using TempDirectory work = TempDirectory.Create();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");
            await transaction.AddAsync("a.txt", content);
            throw new InvalidOperationException("original failure");
        });

        Assert.Equal("original failure", error.Message);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Discard deletes the backups that are an operation's .txnew with .prev appended, and does not search for backups not in the operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add of a.txt, its .txnew.prev, and a .txnew.prev with the same transaction ID in another folder that is not in the operations.</para>
    /// <para>When: DisposeAsync is called.</para>
    /// <para>Then: the Add's .txnew, .prev, and the journal are deleted, and the .prev not in the operations remains (the work folder is not walked).</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DeletesOnlyOperationBackupsWithoutWalkingWorkFolder()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string other = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(other);
        string backup;
        string unrelated;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.AddAsync("a.txt", content);
            string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
            backup = staging + ".prev";
            await File.WriteAllTextAsync(backup, "old");
            string journal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
            string id = System.IO.Path.GetFileNameWithoutExtension(journal).Substring("tx-".Length);
            unrelated = System.IO.Path.Combine(other, "b.txt." + id + ".txnew.prev");
            await File.WriteAllTextAsync(unrelated, "keep");
        }

        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(File.Exists(unrelated));
    }
}
