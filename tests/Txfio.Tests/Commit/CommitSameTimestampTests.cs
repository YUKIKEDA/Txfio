using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitSameTimestampTests
{
    /// <summary>
    /// An Update with the same length and the same last write time still makes the real file the new content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt contains old. The Update's .txnew has the same length, and its last write time matches the real file.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded. The real file becomes new, and the .txnew is gone.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateWithSameTimestampWritesNewContent()
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
    /// Even if an Update with the same last write time is stopped, Recover makes it the new content.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt contains old. The Update's .txnew has the same length, and its last write time matches the real file.</para>
    /// <para>When: CommitAsync is stopped at AfterCommitting, then RecoverAsync runs.</para>
    /// <para>Then: right after the stop, the content is still old and the .txnew remains. After Recover, RolledForward and the real file is new.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_RecoverWritesNewContentForStoppedUpdateWithSameTimestamp()
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
