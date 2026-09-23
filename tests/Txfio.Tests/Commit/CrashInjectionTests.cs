using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CrashInjectionTests : IDisposable
{
    public CrashInjectionTests()
    {
        CrashInjector.Reset();
    }

    public void Dispose()
    {
        CrashInjector.Reset();
    }

    /// <summary>
    /// AfterCommitting で止めても Recover が Add を確定する
    /// </summary>
    /// <remarks>
    /// <para>前提: Add をステージングしている</para>
    /// <para>手順: AfterCommitting で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後は対象が無く .txnew が残り、Recover 後は内容が確定して journal も .txnew も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AfterCommittingで止まってもRecoverがAddを確定すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Add の AfterApply から Recover が完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: Add をステージングしている</para>
    /// <para>手順: AfterApply で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後から対象は Add の内容で、Recover 後に journal が無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AddのAfterApplyからRecoverが完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        CrashInjector.Arm(CrashInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("staged");
            await tx.AddAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("staged", await File.ReadAllTextAsync(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Update の AfterApply から Recover が完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Update している</para>
    /// <para>手順: AfterApply で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後から対象は Update の内容で、Recover 後に journal が無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_UpdateのAfterApplyからRecoverが完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "old");
        CrashInjector.Arm(CrashInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.UpdateAsync("a.txt", content);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// ファイル Delete の AfterApply から Recover が完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 既存ファイルを Delete している</para>
    /// <para>手順: AfterApply で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後から対象は無く、Recover 後も対象は無く journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DeleteのAfterApplyからRecoverが完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string target = System.IO.Path.Combine(work.Path, "a.txt");
        await File.WriteAllTextAsync(target, "gone");
        CrashInjector.Arm(CrashInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.DeleteAsync("a.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(target));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(target));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// Move の AfterApply から Recover が完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルを Move している</para>
    /// <para>手順: AfterApply で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後から先に内容があり元は無く、Recover 後に journal が無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_MoveのAfterApplyからRecoverが完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string dest = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "moved");
        CrashInjector.Arm(CrashInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await tx.MoveAsync("a.txt", "b.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.False(File.Exists(source));
        Assert.Equal("moved", await File.ReadAllTextAsync(dest));
        Assert.Empty(Journals(work.Path));
    }

    /// <summary>
    /// 1つ目の AfterApply で止めても Recover が2つ目を完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: Add と別ファイルの Delete をしている</para>
    /// <para>手順: AfterApply で CommitAsync を止め、RecoverAsync する</para>
    /// <para>期待: 停止直後は Add だけ反映され、Recover 後は Delete も反映されて journal が無い</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_1つ目のAfterApplyからRecoverが2つ目も完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string added = System.IO.Path.Combine(work.Path, "a.txt");
        string deleted = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(deleted, "keep");
        CrashInjector.Arm(CrashInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("added");
            await tx.AddAsync("a.txt", content);
            await tx.DeleteAsync("b.txt");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("added", await File.ReadAllTextAsync(added));
        Assert.Equal("keep", await File.ReadAllTextAsync(deleted));
        Assert.Single(Journals(work.Path));

        RecoverResult result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result);
        Assert.Equal("added", await File.ReadAllTextAsync(added));
        Assert.False(File.Exists(deleted));
        Assert.Empty(Journals(work.Path));
    }

    private static string[] Journals(string workFolder)
    {
        return Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal");
    }
}
