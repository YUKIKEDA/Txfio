using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitReportTests
{
    /// <summary>
    /// A successful commit has an empty list of operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Add in an empty work folder.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and Operations is empty.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SuccessHasEmptyOperations()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "hello");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Empty(report.Operations);
    }

    /// <summary>
    /// Every operation rejected by the check is listed, and after fixing, the same transaction can commit again.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a.txt and b.txt are added, both are created externally.</para>
    /// <para>When: CommitAsync runs, the external files are deleted, and CommitAsync runs again.</para>
    /// <para>Then: the first is Failed with AlreadyExists for both, the second is Succeeded, and the targets have the Add content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SameTransactionCommitsAfterFixingFailedCheck()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");
        string first = System.IO.Path.Combine(work.Path, "a.txt");
        string second = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(first, "external-a");
        await File.WriteAllTextAsync(second, "external-b");

        CommitReport failed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, failed.Result);
        Assert.Equal(2, failed.Operations.Count);
        Assert.Equal(PendingChangeKind.Add, failed.Operations[0].Kind);
        Assert.Equal(OperationDisposition.Rejected, failed.Operations[0].Disposition);
        Assert.Equal(OperationFailureReason.AlreadyExists, failed.Operations[0].Reason);
        Assert.EndsWith("a.txt", failed.Operations[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Null(failed.Operations[0].NewPath);
        Assert.Equal(OperationFailureReason.AlreadyExists, failed.Operations[1].Reason);
        Assert.EndsWith("b.txt", failed.Operations[1].Path, StringComparison.OrdinalIgnoreCase);
        File.Delete(first);
        File.Delete(second);

        CommitReport again = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, again.Result);
        Assert.Empty(again.Operations);
        Assert.Equal("a", await File.ReadAllTextAsync(first));
        Assert.Equal("b", await File.ReadAllTextAsync(second));
    }

    /// <summary>
    /// A Move whose destination already exists lists the destination path and AlreadyExists.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Move, the destination is created externally.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, NewPath is the destination, and the reason is AlreadyExists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ExistingDestinationListsNewPathAndReason()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(dest, "external");

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(PendingChangeKind.Move, operation.Kind);
        Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
        Assert.Equal(OperationFailureReason.AlreadyExists, operation.Reason);
        Assert.Equal(source, operation.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dest, operation.NewPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A directory swapped for a file is ReplacedByFile.
    /// </summary>
    /// <remarks>
    /// <para>Given: after CreateDirectory, the path is made a file.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, and the reason is ReplacedByFile.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SwappedForFileIsReplacedByFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string dir = System.IO.Path.Combine(work.Path, "drop");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CreateDirectoryAsync("drop");
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "file");

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(PendingChangeKind.CreateDirectory, operation.Kind);
        Assert.Equal(OperationFailureReason.ReplacedByFile, operation.Reason);
    }

    /// <summary>
    /// A file swapped for a directory is ReplacedByFile.
    /// </summary>
    /// <remarks>
    /// <para>Given: the targets of an Update, a file Delete, and a file Move are each made a directory.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, all three are Rejected, and the reason is ReplacedByFile.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_SwappedForDirectoryIsReplacedByFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "b.txt");
        string moved = System.IO.Path.Combine(work.Path, "c.txt");
        await File.WriteAllTextAsync(updated, "old");
        await File.WriteAllTextAsync(deleted, "old");
        await File.WriteAllTextAsync(moved, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        await tx.DeleteAsync("b.txt");
        await tx.MoveAsync("c.txt", "d.txt");
        File.Delete(updated);
        File.Delete(deleted);
        File.Delete(moved);
        Directory.CreateDirectory(updated);
        Directory.CreateDirectory(deleted);
        Directory.CreateDirectory(moved);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(3, report.Operations.Count);
        Assert.All(
            report.Operations,
            operation =>
            {
                Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
                Assert.Equal(OperationFailureReason.ReplacedByFile, operation.Reason);
            });
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Update);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Delete);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Move);
    }

    /// <summary>
    /// A deleted file is Missing, and a directory with an unexpected direct child is DirectoryPreconditions.
    /// </summary>
    /// <remarks>
    /// <para>Given: the Update target is deleted, and a file is added externally directly under an empty directory scheduled for Delete.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed; the Update is Missing, and the Delete is DirectoryPreconditions.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MissingTargetAndDirectChildrenHaveDifferentReasons()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        string dir = System.IO.Path.Combine(work.Path, "sub");
        await File.WriteAllTextAsync(file, "old");
        Directory.CreateDirectory(dir);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        await tx.DeleteAsync("sub");
        File.Delete(file);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, "external.txt"), "no");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Contains(
            report.Operations,
            operation => operation.Kind == PendingChangeKind.Update
                && operation.Reason == OperationFailureReason.Missing);
        Assert.Contains(
            report.Operations,
            operation => operation.Kind == PendingChangeKind.Delete
                && operation.Reason == OperationFailureReason.DirectoryPreconditions);
    }

    /// <summary>
    /// A sharing violation when applying a Delete is PartialConflict, and the same instance cannot retry.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Delete, the target file is kept open without sharing.</para>
    /// <para>When: CommitAsync runs, then CommitAsync runs again on the same transaction.</para>
    /// <para>Then: PartialConflict with the reason SharingViolation; the second call throws InvalidOperationException.</para>
    /// </remarks>
    [WindowsFact("An open file cannot be deleted on Windows (Linux unlink ignores open handles)")]
    public async Task CommitAsync_SharingViolationIsPartialConflictAndCannotRetry()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await using FileStream locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        CommitReport report = await tx.CommitAsync();

        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(CommitResult.PartialConflict, report.Result);
        Assert.Equal(OperationDisposition.Skipped, operation.Disposition);
        Assert.Equal(OperationFailureReason.SharingViolation, operation.Reason);
        Assert.Equal(PendingChangeKind.Delete, operation.Kind);
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync());
        Assert.Equal("This transaction has already been committed", again.Message);
    }

    /// <summary>
    /// A check failure because .txnew cannot be read as a file is IoFailure.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Add and an Update, both .txnew files are deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, both are Rejected with IoFailure, and the Update target keeps its original content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UnreadableTxnewIsIoFailure()
    {
        await using TempDirectory work = TempDirectory.Create();
        string updated = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(updated, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "added");
        await tx.WriteAllTextAsync("b.txt", "updated");
        foreach (string staging in Directory.GetFiles(work.Path, "*.txnew"))
        {
            File.Delete(staging);
        }

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(
            report.Operations,
            operation =>
            {
                Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
                Assert.Equal(OperationFailureReason.IoFailure, operation.Reason);
            });
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Add);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Update);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(updated));
    }

    /// <summary>
    /// An Update of a read-only file is rejected by the check as ReadOnly, and can be retried after clearing the attribute.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, a.txt is made read-only.</para>
    /// <para>When: CommitAsync runs, the attribute is cleared, and CommitAsync runs again.</para>
    /// <para>Then: the first is Failed with the reason ReadOnly and disposition Rejected, and a.txt keeps its original content; the second is Succeeded with the new content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateOfReadOnlyFileIsRejectedAndCanRetry()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "new");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            CommitReport report = await tx.CommitAsync();

            OperationReport operation = Assert.Single(report.Operations);
            Assert.Equal(CommitResult.Failed, report.Result);
            Assert.Equal(OperationFailureReason.ReadOnly, operation.Reason);
            Assert.Equal(OperationDisposition.Rejected, operation.Disposition);
            Assert.Equal("old", await File.ReadAllTextAsync(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        CommitReport retried = await tx.CommitAsync();
        Assert.Equal(CommitResult.Succeeded, retried.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// A Delete of a read-only file is rejected by the check as ReadOnly.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a.txt is deleted, a.txt is made read-only.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with the reason ReadOnly, and a.txt remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteOfReadOnlyFileIsRejected()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            CommitReport report = await tx.CommitAsync();

            Assert.Equal(CommitResult.Failed, report.Result);
            Assert.Equal(OperationFailureReason.ReadOnly, Assert.Single(report.Operations).Reason);
            Assert.True(File.Exists(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// A Move is not rejected even for a read-only file.
    /// </summary>
    /// <remarks>
    /// <para>Given: a read-only a.txt exists.</para>
    /// <para>When: Move(a.txt→b.txt), then CommitAsync.</para>
    /// <para>Then: Succeeded, and b.txt exists.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveOfReadOnlyFileIsNotRejected()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "old");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await tx.MoveAsync("a.txt", "b.txt");

            Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
            Assert.True(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(source))
            {
                File.SetAttributes(source, FileAttributes.Normal);
            }

            if (File.Exists(dest))
            {
                File.SetAttributes(dest, FileAttributes.Normal);
            }
        }
    }
}
