using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDirectoryAttachTests
{
    /// <summary>
    /// 未コミットのディレクトリ Attach は Recover で対象を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、ディレクトリ Attach の journal とディレクトリが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのディレクトリAttachは対象を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDirectoryAttachAsync(work.Path, committing: false);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.True(Directory.Exists(target));
    }

    /// <summary>
    /// Committing でディレクトリが残っていれば適用済みとして進める
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing のディレクトリ Attach journal とディレクトリがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で journal は消え、ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのディレクトリAttachは残してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDirectoryAttachAsync(work.Path, committing: true);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing で対象がファイルにすり替わっていると競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing のディレクトリ Attach journal があり、同じパスがファイルである</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected で journal とファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ファイルにすり替わるとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDirectoryAttachAsync(work.Path, committing: true);
        Directory.Delete(target);
        await File.WriteAllTextAsync(target, "file");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.NotEmpty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    private static async Task<string> WriteDirectoryAttachAsync(string workFolder, bool committing)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string targetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, "sub"));
        Directory.CreateDirectory(targetPath);

        string states = string.Empty;
        if (committing)
        {
            states = ",\"before\":" + SnapshotJson.Directory + ",\"after\":" + SnapshotJson.Directory;
        }

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Attach\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"isDirectory\":true" + states + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return targetPath;
    }
}
