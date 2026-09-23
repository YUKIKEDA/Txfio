using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverBeforeAfterTests
{
    /// <summary>
    /// After と一致する Add は .txnew を消して RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Add があり、対象は After どおりで .txnew も残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で journal も .txnew も無く、対象は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_After一致のAddはtxnewを消してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        string staging = target + "." + transactionId.ToString("D") + ".txnew";
        await File.WriteAllTextAsync(staging, "done");
        File.Move(staging, target);
        await File.WriteAllTextAsync(staging, "stale");
        string journal = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string after = SnapshotJson.File(target);
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":true,\"operations\":[{\"kind\":\"Add\",\"path\":" + JsonSerializer.Serialize(target) +
            ",\"stagingPath\":" + JsonSerializer.Serialize(staging) +
            ",\"before\":" + SnapshotJson.Absent + ",\"after\":" + after + "}]}";
        await File.WriteAllTextAsync(journal, json);

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(staging));
        Assert.Equal("done", await File.ReadAllTextAsync(target));
    }
}
