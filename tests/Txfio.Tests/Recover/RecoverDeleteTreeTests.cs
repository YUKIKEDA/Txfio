using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverDeleteTreeTests
{
    /// <summary>
    /// 未コミットの全削除は Recover で対象を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、DeleteTree の journal と中身があるディレクトリが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、ディレクトリと子は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのDeleteTreeは対象を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: false);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(System.IO.Path.Combine(target, "a.txt")));
    }

    /// <summary>
    /// Committing でディレクトリが残っていれば配下ごと消して進める
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の DeleteTree journal と、子ファイルがあるディレクトリがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward でディレクトリも journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのDeleteTreeを完了してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: true);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing で対象がファイルにすり替わっていると競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の DeleteTree journal があり、同じパスがファイルである</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected でファイルは残り、journal は消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ファイルにすり替わるとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteDeleteTreeAsync(work.Path, committing: true);
        Directory.Delete(target, recursive: true);
        await File.WriteAllTextAsync(target, "file");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.ConflictDetected, result);
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
