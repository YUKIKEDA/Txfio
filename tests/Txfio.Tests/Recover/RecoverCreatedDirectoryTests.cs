using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverCreatedDirectoryTests
{
    /// <summary>
    /// 落ちたディレクトリコピーの先と .txnew を Recover が消す
    /// </summary>
    /// <remarks>
    /// <para>前提: src/sub/a.txt がある</para>
    /// <para>手順: dst へ CopyAsync し、ロールバックせず破棄してから RecoverAsync する</para>
    /// <para>期待: コピー中の未確定操作は Add だけ。RolledBack で dst と .txnew は消え、src は残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_落ちたディレクトリコピーの先とtxnewを消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string nested = System.IO.Path.Combine(source, "sub");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "a.txt"), "hello");

        FaultInjector faults = new FaultInjector();
        faults.SuppressRollback();
        await using (ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await transaction.CopyAsync("src", "dst");
            PendingChange pending = Assert.Single(transaction.GetPendingChanges());
            Assert.Equal(PendingChangeKind.Add, pending.Kind);
        }

        string destination = System.IO.Path.Combine(work.Path, "dst");
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "sub")));
        Assert.NotEmpty(Directory.GetFiles(System.IO.Path.Combine(destination, "sub"), "*.txnew"));

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(nested, "a.txt")));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
    }

    /// <summary>
    /// ファイルの無いコピー先も、落ちたあとに消える
    /// </summary>
    /// <remarks>
    /// <para>前提: 空の src がある</para>
    /// <para>手順: dst へ CopyAsync し、ロールバックせず破棄してから RecoverAsync する</para>
    /// <para>期待: 未確定操作は無い。RolledBack で dst は消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_落ちた空ディレクトリのコピー先を消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "src"));

        FaultInjector faults = new FaultInjector();
        faults.SuppressRollback();
        await using (ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await transaction.CopyAsync("src", "dst");
            Assert.Empty(transaction.GetPendingChanges());
        }

        string destination = System.IO.Path.Combine(work.Path, "dst");
        Assert.True(Directory.Exists(destination));
        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(Directory.Exists(destination));
    }

    /// <summary>
    /// ロールバックは再ステージの退避も消す
    /// </summary>
    /// <remarks>
    /// <para>前提: 未コミットの Add 残骸と、その .txnew.prev がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で .txnew.prev は消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ロールバックでtxnewの退避を消すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        string backup = leftover.StagingPath + ".prev";
        await File.WriteAllTextAsync(backup, "old");

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(leftover.StagingPath));
    }

    /// <summary>
    /// Committing の復旧は、作成ディレクトリを消さない
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Add 残骸と、ジャーナルにだけ載った作成ディレクトリがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledForward で Add は確定し、作成ディレクトリは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ロールフォワードでは作成ディレクトリを残すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        string made = System.IO.Path.Combine(work.Path, "made");
        Directory.CreateDirectory(made);
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        json = json.Replace(
            "\"operations\"",
            "\"createdDirectories\":[" + JsonSerializer.Serialize(made) + "],\"operations\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json);

        Assert.Equal(RecoverResult.RolledForward, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Equal("staged", await File.ReadAllTextAsync(leftover.TargetPath));
        Assert.True(Directory.Exists(made));
    }
}
