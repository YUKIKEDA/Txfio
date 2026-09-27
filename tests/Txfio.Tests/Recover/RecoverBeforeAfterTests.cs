using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverBeforeAfterTests
{
    /// <summary>
    /// An Add that matches After deletes the .txnew and is RolledForward.
    /// </summary>
    /// <remarks>
    /// <para>Given: a Committing Add, whose target matches After while its .txnew still remains.</para>
    /// <para>When: RecoverAsync runs.</para>
    /// <para>Then: RolledForward, there is no journal and no .txnew, and the target remains.</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_AddMatchingAfterDeletesTxnewAndRollsForward()
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

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(staging));
        Assert.Equal("done", await File.ReadAllTextAsync(target));
    }
}
