using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDeleteTreeTests
{
    /// <summary>
    /// Recover of an uncommitted DeleteTree keeps the target.
    /// </summary>
    /// <remarks>
    /// <para>Given: no live transaction, and a DeleteTree journal and a directory with contents remain.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledBack, the journal is deleted, and the directory and its children remain.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_UncommittedDeleteTreeKeepsTargetAndRollsBack()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: false);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(System.IO.Path.Combine(target, "a.txt")));
    }

    /// <summary>
    /// With Committing, if the directory remains, it is deleted with everything under it.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing DeleteTree journal and a directory with a child file.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, and neither the directory nor the journal exists.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_FinishesCommittingDeleteTreeAndRollsForward()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// With Committing, a target swapped for a file is a conflict.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing DeleteTree journal exists, and the same path is a file.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: ConflictDetected, the file remains, and the journal is deleted.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_SwappedForFileIsConflictDetected()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: true);
        Directory.Delete(target, recursive: true);
        await File.WriteAllTextAsync(target, "file");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result.Result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    private static async Task<string> WriteDeleteTreeAsync(string workFolder, bool committing)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string targetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, "tree"));
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(System.IO.Path.Combine(targetPath, "a.txt"), "keep");

        string states = string.Empty;
        if (committing)
        {
            states = ",\"before\":" + SnapshotJson.Directory + ",\"after\":" + SnapshotJson.Absent;
        }

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"DeleteTree\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"isDirectory\":true" + states + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return targetPath;
    }
}
