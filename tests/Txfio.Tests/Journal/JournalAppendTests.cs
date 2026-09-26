using Txfio.Tests.Support;

namespace Txfio.Tests.Journal;

public sealed class JournalAppendTests
{
    /// <summary>
    /// 末尾に足すだけの操作は、ジャーナルへ 1 行ずつ追記する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダだけがある</para>
    /// <para>手順: a.txt、b.txt、c.txt を順に Add し、ジャーナルの行数を数える</para>
    /// <para>期待: 1 行目の文書と追記 3 行の計 4 行であり、操作の path は a.txt、b.txt、c.txt である</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_末尾に足すだけならジャーナルへ追記すること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);

        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");
        await tx.WriteAllTextAsync("c.txt", "c");

        string journal = await File.ReadAllTextAsync(JournalOf(work.Path));
        Assert.Equal(4, journal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"path\":\"a.txt\"", journal, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"b.txt\"", journal, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"c.txt\"", journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// 途中が変わる操作は、ジャーナルを 1 行の文書に書き直す
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と b.txt を Add した</para>
    /// <para>手順: a.txt を Delete して Add を打ち消す</para>
    /// <para>期待: ジャーナルは 1 行であり、操作は b.txt の Add だけ</para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_途中が変わるとジャーナルを書き直すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.WriteAllTextAsync("a.txt", "a");
        await tx.WriteAllTextAsync("b.txt", "b");

        await tx.DeleteAsync("a.txt");

        Assert.Single(await File.ReadAllLinesAsync(JournalOf(work.Path)));
        Assert.Equal(System.IO.Path.Combine(work.Path, "b.txt"), Assert.Single(tx.GetPendingChanges()).Path);
    }

    /// <summary>
    /// 追記したジャーナルも、Recover は追記した操作ごと巻き戻す
    /// </summary>
    /// <remarks>
    /// <para>前提: 3 件 Add したあと、ロールバックせずに Dispose した（落ちたのと同じ）</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で .txnew は 1 つも残らず、ジャーナルも無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_追記したジャーナルを巻き戻すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            await tx.WriteAllTextAsync("b.txt", "b");
            await tx.WriteAllTextAsync("c.txt", "c");
            faults.SuppressRollback();
        }

        Assert.Equal(3, Directory.GetFiles(work.Path, "*.txnew").Length);

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// 改行で終わっていない最後の行は、追記の途中で落ちたものとして捨てる
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add したジャーナルの末尾に、改行の無い書きかけの行がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: RolledBack で a.txt の .txnew は消える</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_書きかけの最後の行は捨てること()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            faults.SuppressRollback();
        }

        await File.AppendAllTextAsync(JournalOf(work.Path), "{\"append\":[{\"kind\":\"Add\",\"pa");

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 改行で終わっているのに読めない行があれば、読めないジャーナルとする
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt を Add したジャーナルの末尾に、改行で終わる壊れた行がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: JournalUnreadable で、ジャーナルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_改行で終わる壊れた行は読めないジャーナルにすること()
    {
        await using TempDirectory work = TempDirectory.Create();
        FaultInjector faults = new FaultInjector();
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults))
        {
            await tx.WriteAllTextAsync("a.txt", "a");
            faults.SuppressRollback();
        }

        string journal = JournalOf(work.Path);
        await File.AppendAllTextAsync(journal, "{broken\n");

        Assert.Equal(RecoverResult.JournalUnreadable, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.True(File.Exists(journal));
    }

    private static string JournalOf(string workFolder)
    {
        return Directory.GetFiles(System.IO.Path.Combine(workFolder, ".txfio"), "tx-*.journal").Single();
    }
}
