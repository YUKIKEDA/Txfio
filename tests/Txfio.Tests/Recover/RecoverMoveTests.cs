using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverMoveTests
{
    /// <summary>
    /// 未コミット Move の Recover は元を残す
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Move の journal と元ファイルが残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で journal は消え、元は残り、先は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_未コミットのMoveは元を残してRolledBackになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: false,
            "a.txt",
            "b.txt",
            "keep");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledBack, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(leftover.SourcePath));
        Assert.False(File.Exists(leftover.DestPath));
    }

    /// <summary>
    /// Committing の Move 残骸は Recover が移動を完了する
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きたトランザクションは無く、Committing の Move journal と元が残っている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で先の内容があり、元も journal も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_CommittingのMoveを完了してRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: true,
            "a.txt",
            "b.txt",
            "gone");

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.Equal("gone", await File.ReadAllTextAsync(leftover.DestPath));
    }

    /// <summary>
    /// Committing で既に移動済みなら Recover は完了扱いで journal を消す
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Move journal があり、元は無く先だけある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で journal は無く先は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_既に移動済みのCommittingはRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverMoveFiles leftover = await LeftoverMoveFiles.WriteMoveAsync(
            work.Path,
            committing: true,
            "a.txt",
            "b.txt",
            "done",
            alreadyMoved: true);

        RecoverReport result = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, result.Result);
        Assert.False(File.Exists(leftover.JournalPath));
        Assert.False(File.Exists(leftover.SourcePath));
        Assert.Equal("done", await File.ReadAllTextAsync(leftover.DestPath));
    }

    /// <summary>
    /// 置き換えの Move を Committing の直後に止めても、Recover が置き換えを終える
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、Move(a.txt→b.txt, overwrite: true) を予約した</para>
    /// <para>手順: Committing の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: RolledForward であり、b.txt は旧 a.txt の中身、a.txt は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_置き換えのMoveを完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal("old", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
    }

    /// <summary>
    /// 置き換えの Move を適用し終えてから落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt があり、Move(a.txt→b.txt, overwrite: true) を予約した</para>
    /// <para>手順: 適用の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: RolledForward であり、飛ばした操作は無く、b.txt は旧 a.txt の中身</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_適用済みの置き換えのMoveはRolledForwardになること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "new");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("a.txt", "b.txt", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Assert.Single(report.Journals).Operations);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt")));
    }

    /// <summary>
    /// ディレクトリの入れ替えを Committing の直後に止めても、Recover が入れ替えを終える
    /// </summary>
    /// <remarks>
    /// <para>前提: site/old.txt と build/new.txt があり、Move(build→site, overwrite: true) を予約した</para>
    /// <para>手順: Committing の直後に止めて Dispose し、RecoverAsync する</para>
    /// <para>期待: RolledForward であり、site には new.txt だけがあり、.txold は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ディレクトリの入れ替えを完了すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "build", "new.txt"), "new");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("build", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal(new[] { "new.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.Empty(Directory.GetDirectories(work.Path, "*.txold"));
    }

    /// <summary>
    /// 移動先を .txold へ退けたところで落ちても、Recover は続きから入れ替える
    /// </summary>
    /// <remarks>
    /// <para>前提: site/old.txt と build/new.txt があり、Move(build→site, overwrite: true) を Committing の直後に止めた</para>
    /// <para>手順: site を site.{txid}.txold へ手で移してから RecoverAsync する</para>
    /// <para>期待: RolledForward であり、site には new.txt だけがあり、build も .txold も無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_退避のあとで落ちた入れ替えを続けること()
    {
        await using TempDirectory work = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "build"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "build", "new.txt"), "new");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.MoveAsync("build", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string id = System.IO.Path.GetFileNameWithoutExtension(journal).Substring("tx-".Length);
        Directory.Move(site, site + "." + id + ".txold");

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal(new[] { "new.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
        Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "build")));
        Assert.Empty(Directory.GetDirectories(work.Path, "*.txold"));
    }

    /// <summary>
    /// Import で作ったディレクトリでの入れ替えをすべて適用してから落ちても、Recover は RolledForward になる
    /// </summary>
    /// <remarks>
    /// <para>前提: site/old.txt と、外の incoming/a.txt がある。incoming を site.new へ Import し、Move(site.new→site, overwrite: true) を予約した</para>
    /// <para>手順: 最初の適用（Add）の直後に止め、入れ替えを手で済ませてから RecoverAsync する</para>
    /// <para>期待: RolledForward であり、飛ばした操作は無く、site には a.txt だけがある</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_入れ替え済みなら移動元の配下のAddも済んだとみなすこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string site = System.IO.Path.Combine(work.Path, "site");
        string staged = System.IO.Path.Combine(work.Path, "site.new");
        Directory.CreateDirectory(site);
        await File.WriteAllTextAsync(System.IO.Path.Combine(site, "old.txt"), "old");
        string incoming = System.IO.Path.Combine(outside.Path, "incoming");
        Directory.CreateDirectory(incoming);
        await File.WriteAllTextAsync(System.IO.Path.Combine(incoming, "a.txt"), "alpha");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterApply);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.ImportAsync(incoming, "site.new");
            await tx.MoveAsync("site.new", "site", overwrite: true);
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Directory.Delete(site, recursive: true);
        Directory.Move(staged, site);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);
        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Empty(Assert.Single(report.Journals).Operations);
        Assert.Equal(new[] { "a.txt" }, Directory.GetFileSystemEntries(site).Select(System.IO.Path.GetFileName).ToArray());
    }
}
