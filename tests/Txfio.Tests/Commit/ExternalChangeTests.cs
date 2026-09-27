using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class ExternalChangeTests
{
    /// <summary>
    /// By default, an Update whose content alone changed after staging does not fail.
    /// </summary>
    /// <remarks>
    /// <para>Given: an Update without detectExternalChanges, after which the real file's content changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the file has the staged content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ByDefaultContentOnlyChangeDoesNotFail()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Empty(report.Operations);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// An Update whose size differs fails with ExternalChange, and can commit after the real file is restored.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, and after an Update the real file's size changes.</para>
    /// <para>When: CommitAsync runs, the real file's content and last write time are restored, and CommitAsync runs again.</para>
    /// <para>Then: the first is Failed with ExternalChange (the real file keeps the external content), and the second is Succeeded.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DifferentSizeFailsWithExternalChangeAndCommitsAfterFix()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");

        CommitReport failed = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, failed.Result);
        OperationReport rejected = Assert.Single(failed.Operations);
        Assert.Equal(PendingChangeKind.Update, rejected.Kind);
        Assert.Equal(OperationDisposition.Rejected, rejected.Disposition);
        Assert.Equal(OperationFailureReason.ExternalChange, rejected.Reason);
        Assert.EndsWith("a.txt", rejected.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, "hello");
        File.SetLastWriteTimeUtc(file, stamp);

        CommitReport again = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, again.Result);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// A difference only in the last write time is ExternalChange too.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, and after an Update only the real file's last write time moves forward.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with ExternalChange, and the real file's content does not change.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DifferentLastWriteTimeIsExternalChange()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, TimeSpan.Zero, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.Equal("hello", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// A rewrite with the same size and the same last write time is missed.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, and after an Update the file is rewritten with different content of the same length, with the last write time restored.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the file has the staged content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MissesSameSizeAndSameLastWriteTime()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "world");
        await File.WriteAllTextAsync(file, "HELLO");
        File.SetLastWriteTimeUtc(file, stamp);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("world", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// Every differing Update is listed in Operations.
    /// </summary>
    /// <remarks>
    /// <para>Given: after two files are updated, both change size.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, both are ExternalChange, and the real files keep the external content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ListsEveryDifferingUpdate()
    {
        await using TempDirectory work = TempDirectory.Create();
        string first = System.IO.Path.Combine(work.Path, "a.txt");
        string second = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "A");
        await tx.WriteAllTextAsync("b.txt", "B");
        await File.WriteAllTextAsync(first, "aa");
        await File.WriteAllTextAsync(second, "bb");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(report.Operations, operation => Assert.Equal(OperationFailureReason.ExternalChange, operation.Reason));
        Assert.Equal("aa", await File.ReadAllTextAsync(first));
        Assert.Equal("bb", await File.ReadAllTextAsync(second));
    }

    /// <summary>
    /// When the file is missing, it stays Missing.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the real file is gone.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with Missing.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MissingFileStaysMissing()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.Delete(file);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.Missing, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// When it has become a directory, it stays ReplacedByFile.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the real file becomes a directory.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with ReplacedByFile.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DirectoryStaysReplacedByFile()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        File.Delete(file);
        Directory.CreateDirectory(file);

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ReplacedByFile, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// For a path with a Read record, staging after the real file changes does not update the record.
    /// </summary>
    /// <remarks>
    /// <para>Given: after ReadAsync, the real file's size changes, and then it is updated.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with ExternalChange.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_RestageDoesNotUpdateReadRecord()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await using (Stream stream = await tx.ReadAsync("a.txt"))
        {
        }

        await File.WriteAllTextAsync(file, "external-longer");
        await tx.WriteAllTextAsync("a.txt", "next");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// A Read of the real file updates the record to that moment.
    /// </summary>
    /// <remarks>
    /// <para>Given: after a Read, the real file's size changes, then it is read again and updated.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the file has the staged content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ReadOfRealFileUpdatesRecord()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await using (Stream first = await tx.ReadAsync("a.txt"))
        {
        }

        await File.WriteAllTextAsync(file, "external-longer");
        await using (Stream second = await tx.ReadAsync("a.txt"))
        {
        }

        await tx.WriteAllTextAsync("a.txt", "next");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("next", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// Reading this transaction's staging file (.txnew) does not update the record.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the real file's size changes, and ReadAsync reads the .txnew.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with ExternalChange, and the real file keeps the external content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ReadOfTxnewDoesNotUpdateRecord()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");
        await using (Stream stream = await tx.ReadAsync("a.txt"))
        {
            using StreamReader reader = new StreamReader(stream);
            Assert.Equal("next", await reader.ReadToEndAsync());
        }

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// An Update without a Read updates the record at each restage.
    /// </summary>
    /// <remarks>
    /// <para>Given: after an Update, the real file's size changes, and it is updated again.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and the file has the restaged content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateWithoutReadUpdatesRecordOnRestage()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.WriteAllTextAsync("a.txt", "next");
        await File.WriteAllTextAsync(file, "external-longer");
        await tx.WriteAllTextAsync("a.txt", "later");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, report.Result);
        Assert.Equal("later", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// After folding an Update at a Move destination, the remaining operations are ExternalChange if the original real file differs.
    /// </summary>
    /// <remarks>
    /// <para>Given: after the Move destination is updated, the source's size changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed, the Add and Delete are ExternalChange, the source keeps the external content, and the destination does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FoldedUpdateFailsWhenOriginalFileDiffers()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.WriteAllTextAsync("b.txt", "next");
        await File.WriteAllTextAsync(source, "external-longer");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(2, report.Operations.Count);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Add && operation.Reason == OperationFailureReason.ExternalChange);
        Assert.Contains(report.Operations, operation => operation.Kind == PendingChangeKind.Delete && operation.Reason == OperationFailureReason.ExternalChange);
        Assert.Equal("external-longer", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// A Delete of a file rewritten after staging fails with ExternalChange.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, and after a.txt is deleted the real file's size changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with the reason ExternalChange, and a.txt remains with the external content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteOfRewrittenFileFailsWithExternalChange()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.DeleteAsync("a.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        OperationReport operation = Assert.Single(report.Operations);
        Assert.Equal(OperationFailureReason.ExternalChange, operation.Reason);
        Assert.Equal(PendingChangeKind.Delete, operation.Kind);
        Assert.Equal("external change", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// A Move of a file rewritten after staging fails with ExternalChange.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, and after Move(a.txt→b.txt) the size of a.txt changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with the reason ExternalChange, a.txt remains, and b.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveOfRewrittenFileFailsWithExternalChange()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// A Delete of a file rewritten after it was read also fails, compared with the time it was read.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, a.txt is read, the real file's size changes, and then it is deleted.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with the reason ExternalChange.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteOfFileRewrittenAfterReadFails()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        Assert.Equal("hello", await tx.ReadAllTextAsync("a.txt"));
        await File.WriteAllTextAsync(file, "external change");
        await tx.DeleteAsync("a.txt");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
    }

    /// <summary>
    /// Even when a Delete of the destination after a Move folds into a Delete of the source, the record of the original file is compared.
    /// </summary>
    /// <remarks>
    /// <para>Given: detectExternalChanges is true, b.txt is deleted after Move(a.txt→b.txt) (folding into a Delete of the source), and the size of a.txt changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Failed with the reason ExternalChange, and a.txt remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FoldedDeleteComparesOriginalFileRecord()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, detectExternalChanges: true);
        await tx.MoveAsync("a.txt", "b.txt");
        await tx.DeleteAsync("b.txt");
        await File.WriteAllTextAsync(file, "external change");

        CommitReport report = await tx.CommitAsync();

        Assert.Equal(CommitResult.Failed, report.Result);
        Assert.Equal(OperationFailureReason.ExternalChange, Assert.Single(report.Operations).Reason);
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// By default, a Delete of a file rewritten after staging does not fail.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is deleted without detectExternalChanges, and then the real file's size changes.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and a.txt does not exist.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_ByDefaultDeleteOfRewrittenFileDoesNotFail()
    {
        await using TempDirectory work = TempDirectory.Create();
        string file = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await File.WriteAllTextAsync(file, "external change");

        Assert.Equal(CommitResult.Succeeded, (await tx.CommitAsync()).Result);
        Assert.False(File.Exists(file));
    }
}
