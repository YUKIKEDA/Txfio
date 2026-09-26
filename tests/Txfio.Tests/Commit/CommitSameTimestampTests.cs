using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitSameTimestampTests
{
    /// <summary>
    /// 同じ長さで更新時刻も同じ Update は、本物を新しい内容にする
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の内容は old。Update の .txnew は同じ長さで、最終更新日時を本物と揃えてある</para>
    /// <para>手順: CommitAsync する</para>
    /// <para>期待: Succeeded。本物は new になり、.txnew は消える</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_更新時刻が同じUpdateでも新しい内容になること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await transaction.UpdateAsync("a.txt", content);
        string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(target));

        CommitReport result = await transaction.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 同じ更新時刻の Update を止めても、Recover が新しい内容にする
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の内容は old。Update の .txnew は同じ長さで、最終更新日時を本物と揃えてある</para>
    /// <para>手順: AfterCommitting で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 止めた直後は old のまま .txnew が残る。Recover のあと RolledForward で本物は new</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_更新時刻が同じUpdateを止めてもRecoverが新しい内容にすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await transaction.UpdateAsync("a.txt", content);
            string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
            File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(target));
            await Assert.ThrowsAsync<CrashInjectionException>(() => transaction.CommitAsync());
        }

        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }
}
