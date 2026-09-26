using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Journal;

public sealed class JournalPathsTests
{
    /// <summary>
    /// ジャーナルには、ワークフォルダからの相対パスを書く
    /// </summary>
    /// <remarks>
    /// <para>前提: サブフォルダ sub がある</para>
    /// <para>手順: sub/a.txt を Add し、ジャーナルを読む</para>
    /// <para>期待: ジャーナルにワークフォルダの絶対パスは無く、sub と a.txt の相対パスがある</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_ジャーナルには相対パスを書くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "sub"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        await tx.AddAsync("sub/a.txt", content);

        string journal = Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal").Single();
        string text = await File.ReadAllTextAsync(journal);
        Assert.DoesNotContain(JsonSerializer.Serialize(work.Path).Trim('"'), text, StringComparison.OrdinalIgnoreCase);
        string relative = JsonSerializer.Serialize(System.IO.Path.Combine("sub", "a.txt"));
        Assert.Contains("\"path\":" + relative, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ワークフォルダを別の場所へ移してからでも、Recover はロールフォワードできる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の Update を Committing の直後に止めた</para>
    /// <para>手順: ワークフォルダごと別の名前へ移し、移した先で RecoverAsync する</para>
    /// <para>期待: RolledForward で、移した先の a.txt が新しい内容になる</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_ワークフォルダを移したあとでもロールフォワードできること()
    {
        await using TempDirectory root = TempDirectory.Create();
        string before = System.IO.Path.Combine(root.Path, "before");
        string after = System.IO.Path.Combine(root.Path, "after");
        Directory.CreateDirectory(before);
        await File.WriteAllTextAsync(System.IO.Path.Combine(before, "a.txt"), "old");
        FaultInjector faults = new FaultInjector();
        faults.Arm(IFaultInjector.AfterCommitting);
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(before, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "new");
            await Assert.ThrowsAsync<CrashInjectionException>(() => tx.CommitAsync());
        }

        Directory.Move(before, after);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(after);

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(after, "a.txt")));
        Assert.Empty(Directory.GetFiles(after, "*.txnew"));
    }

    /// <summary>
    /// ワークフォルダの外を指すジャーナルは読めないとし、外のファイルを消さない
    /// </summary>
    /// <remarks>
    /// <para>前提: 未コミットの Add のジャーナルで、stagingPath をワークフォルダの外のファイルに書き換えている</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: JournalUnreadable で、ジャーナルと外のファイルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_外を指すジャーナルは読めないとし外のファイルを消さないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string victim = System.IO.Path.Combine(outside.Path, "keep.txt");
        await File.WriteAllTextAsync(victim, "keep");
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "a.txt",
            "staged");
        string json = await File.ReadAllTextAsync(leftover.JournalPath);
        string staging = JsonSerializer.Serialize(leftover.StagingPath);
        Assert.Contains(staging, json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(leftover.JournalPath, json.Replace(staging, JsonSerializer.Serialize(victim), StringComparison.Ordinal));

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.True(File.Exists(leftover.JournalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(victim));
    }
}
