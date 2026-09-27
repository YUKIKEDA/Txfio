using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverCreateDirectoryTests
{
    /// <summary>
    /// Recover deletes an uncommitted CreateDirectory with its contents.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, a CreateDirectory journal, and a child file.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and there is no journal and no directory.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UncommittedDeletesWithContentsAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// An uncommitted journal whose directory does not exist yet can be rolled back too.
    /// </summary>
    /// <remarks>
    /// <para>Given: a CreateDirectory journal exists, and the directory does not.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_RollsBackWhenDirectoryIsMissing()
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: false);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// With Committing, if the directory remains, it proceeds and keeps the contents.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing CreateDirectory journal and a child file.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, the child file remains, and there is no journal.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingWithDirectoryRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "a.txt")));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// With Committing, a missing directory is a conflict.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing CreateDirectory journal exists, and the directory does not.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: ConflictDetected, and the journal is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingWithoutDirectoryIsConflictDetected()
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: false);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, result.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// With Committing, a target swapped for a file is a conflict.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing CreateDirectory journal exists, and the same path is a file.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: ConflictDetected, the file remains, and the journal is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_SwappedForFileIsConflictDetected()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: true);
        Directory.Delete(target, recursive: true);
        await File.WriteAllTextAsync(target, "file");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, result.Result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Recover keeps a CreateDirectory not recorded as created when it has contents.
    /// </summary>
    /// <remarks>
    /// <para>Given: the journal of an uncommitted CreateDirectory is not recorded as created, and a directory with the same name has a child file (someone else created it after the crash).</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the journal is deleted, and the directory and child file remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_KeepsNotCreatedDirectoryWithContents()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: true, directoryCreated: false);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "a.txt")));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Recover deletes an empty directory even when the CreateDirectory is not recorded as created.
    /// </summary>
    /// <remarks>
    /// <para>Given: the journal of an uncommitted CreateDirectory is not recorded as created, and an empty directory with the same name exists (it crashed right after creating it).</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, and there is no directory and no journal.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_DeletesNotCreatedEmptyDirectory()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: false);
        Directory.CreateDirectory(target);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// CreateDirectoryAsync records "created" in the journal after creating the directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: only the work folder exists.</para>
    /// <para>When: CreateDirectoryAsync runs, then the journal is read.</para>
    /// <para>Then: the directory exists, and the journal has directoryCreated set to true.</para>
    /// </remarks>
    [Fact]
    public async Task CreateDirectoryAsync_RecordsCreatedAfterCreating()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.CreateDirectoryAsync("drop");

        Assert.True(Directory.Exists(System.IO.Path.Combine(work.Path, "drop")));
        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        Assert.Contains("\"directoryCreated\":true", await File.ReadAllTextAsync(journal), StringComparison.Ordinal);
    }

    private static async Task<string> WriteCreateDirectoryAsync(
        string workFolder,
        bool committing,
        bool createDirectory,
        bool? directoryCreated = null)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string targetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, "drop"));
        if (createDirectory)
        {
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(System.IO.Path.Combine(targetPath, "a.txt"), "keep");
        }

        string states = string.Empty;
        if (committing)
        {
            states = ",\"before\":" + SnapshotJson.Directory + ",\"after\":" + SnapshotJson.Directory;
        }

        string created = (directoryCreated ?? createDirectory) ? ",\"directoryCreated\":true" : string.Empty;
        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"CreateDirectory\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"isDirectory\":true" + created + states + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return targetPath;
    }
}
