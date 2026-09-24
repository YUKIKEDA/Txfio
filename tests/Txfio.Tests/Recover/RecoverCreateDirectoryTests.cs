using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverCreateDirectoryTests
{
    /// <summary>
    /// 未コミットの CreateDirectory は Recover で中身ごと消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、CreateDirectory の journal と子ファイルがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal とディレクトリが無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットは中身ごと消してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: true);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// ディレクトリがまだ無い未コミットの journal もロールバックできる
    /// </summary>
    /// <remarks>
    /// <para>前提: CreateDirectory の journal があり、ディレクトリは無い</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal が無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ディレクトリが無くてもRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteCreateDirectoryAsync(work.Path, committing: false, createDirectory: false);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, result);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing でディレクトリが残っていれば、中身を残して進める
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の CreateDirectory journal と子ファイルがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で子ファイルは残り、journal は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingで残っていればRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: true);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(target, "a.txt")));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing でディレクトリが無いと競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の CreateDirectory journal があり、ディレクトリは無い</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected で journal は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_Committingで無いとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: false);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.NotEmpty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// Committing で対象がファイルにすり替わっていると競合する
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の CreateDirectory journal があり、同じパスがファイルである</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: ConflictDetected でファイルと journal は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ファイルにすり替わるとConflictDetectedになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = await WriteCreateDirectoryAsync(work.Path, committing: true, createDirectory: true);
        Directory.Delete(target, recursive: true);
        await File.WriteAllTextAsync(target, "file");

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, result);
        Assert.Equal("file", await File.ReadAllTextAsync(target));
        Assert.NotEmpty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    private static async Task<string> WriteCreateDirectoryAsync(
        string workFolder,
        bool committing,
        bool createDirectory)
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

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"CreateDirectory\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"isDirectory\":true" + states + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return targetPath;
    }
}
