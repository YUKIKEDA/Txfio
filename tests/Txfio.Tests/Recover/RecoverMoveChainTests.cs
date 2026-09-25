using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveChainTests : IDisposable
{
    public RecoverMoveChainTests()
    {
        CrashInjector.Reset();
    }

    public void Dispose()
    {
        CrashInjector.Reset();
    }

    /// <summary>
    /// 連鎖の先頭だけ適用して落ちても、Recover が残りの Move を同じ順で終える
    /// </summary>
    /// <remarks>
    /// <para>前提: log.txt と log.1 があり、log.2 は無い</para>
    /// <para>手順: Move(log.1→log.2) と Move(log→log.1) を予約し、最初の適用の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: 止めた時点では log.2 だけが旧 log.1 で、Recover は RolledForward、log.1 は旧 log になり、log.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_連鎖の途中から同じ順で完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string current = System.IO.Path.Combine(work.Path, "log.txt");
        string older = System.IO.Path.Combine(work.Path, "log.1");
        string oldest = System.IO.Path.Combine(work.Path, "log.2");
        await File.WriteAllTextAsync(current, "current");
        await File.WriteAllTextAsync(older, "older");
        CrashInjector.Arm(CrashInjector.AfterApply);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await tx.MoveAsync("log.1", "log.2");
            await tx.MoveAsync("log.txt", "log.1");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }
        finally
        {
            CrashInjector.Reset();
        }

        Assert.Equal("older", await File.ReadAllTextAsync(oldest));
        Assert.False(File.Exists(older));
        Assert.Equal("current", await File.ReadAllTextAsync(current));

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("older", await File.ReadAllTextAsync(oldest));
        Assert.Equal("current", await File.ReadAllTextAsync(older));
        Assert.False(File.Exists(current));
    }
}
