using Txfio.Tests.Support;

namespace Txfio.Tests.Recover;

public sealed class RecoverReportTests
{
    /// <summary>
    /// ConflictDetected は飛ばした操作を載せ、次の Recover はそのジャーナルを含めない
    /// </summary>
    /// <remarks>
    /// <para>前提: Committing の Add 残骸があり、対象パスは外部で既に作られている</para>
    /// <para>手順: RecoverAsync を 2 回呼ぶ</para>
    /// <para>期待: 1 回目は ConflictDetected でそのジャーナルの操作は BeforeAfterMismatch、2 回目は NoPendingTransactions でジャーナル一覧は空</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_競合した操作を結果に載せること()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles leftover = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: true,
            "a.txt",
            "staged");
        await File.WriteAllTextAsync(leftover.TargetPath, "external");
        Assert.True(MetadataNames.TryGetTransactionId(leftover.JournalPath, out Guid transactionId));

        RecoverReport first = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.ConflictDetected, first.Result);
        JournalReport journal = Assert.Single(first.Journals);
        Assert.Equal(transactionId, journal.TransactionId);
        Assert.Equal(RecoverResult.ConflictDetected, journal.Result);
        OperationReport operation = Assert.Single(journal.Operations);
        Assert.Equal(OperationDisposition.Skipped, operation.Disposition);
        Assert.Equal(OperationFailureReason.BeforeAfterMismatch, operation.Reason);
        Assert.Equal(PendingChangeKind.Add, operation.Kind);
        Assert.EndsWith("a.txt", operation.Path, StringComparison.OrdinalIgnoreCase);

        RecoverReport second = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.NoPendingTransactions, second.Result);
        Assert.Empty(second.Journals);
    }

    /// <summary>
    /// 複数ジャーナルは全体の優先順位と、ジャーナルごとの結果を両方返す
    /// </summary>
    /// <remarks>
    /// <para>前提: 壊れたジャーナルと、未コミットの Add 残骸がある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: 全体は JournalUnreadable、壊れた方の操作一覧は空で、未コミットの方は RolledBack</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_複数ジャーナルは優先順位と内訳を返すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        LeftoverAddFiles broken = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "broken.txt",
            "staged");
        await File.WriteAllTextAsync(broken.JournalPath, "{\"version\":1,\"transac");
        LeftoverAddFiles pending = await LeftoverAddFiles.WriteAddAsync(
            work.Path,
            committing: false,
            "pending.txt",
            "staged");
        Assert.True(MetadataNames.TryGetTransactionId(broken.JournalPath, out Guid brokenId));
        Assert.True(MetadataNames.TryGetTransactionId(pending.JournalPath, out Guid pendingId));

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.Equal(2, report.Journals.Count);
        JournalReport unreadable = Assert.Single(report.Journals, journal => journal.TransactionId == brokenId);
        Assert.Equal(RecoverResult.JournalUnreadable, unreadable.Result);
        Assert.Empty(unreadable.Operations);
        JournalReport rolledBack = Assert.Single(report.Journals, journal => journal.TransactionId == pendingId);
        Assert.Equal(RecoverResult.RolledBack, rolledBack.Result);
        Assert.Empty(rolledBack.Operations);
    }

    /// <summary>
    /// 生きているジャーナルは一覧に入れない
    /// </summary>
    /// <remarks>
    /// <para>前提: 操作していない生きているトランザクションと、AfterCommitting で止めて Dispose したトランザクションがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: 一覧は落ちた方だけで、生きているトランザクション ID は無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_生きているジャーナルは一覧に入れないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        string liveJournal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(MetadataNames.TryGetTransactionId(liveJournal, out Guid liveId));
        CrashInjector.Arm(CrashInjector.AfterCommitting);
        try
        {
            await using ITransaction crashed = await global::Txfio.Txfio.BeginAsync(work.Path);
            await crashed.WriteAllTextAsync("a.txt", "crashed");
            await Assert.ThrowsAsync<CrashInjectionException>(() => crashed.CommitAsync());
        }
        finally
        {
            CrashInjector.Reset();
        }

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledForward, report.Result);
        JournalReport journal = Assert.Single(report.Journals);
        Assert.NotEqual(liveId, journal.TransactionId);
        Assert.Equal(RecoverResult.RolledForward, journal.Result);
        Assert.Empty(journal.Operations);
        Assert.Empty(live.GetPendingChanges());
    }

    /// <summary>
    /// 複数の残骸ジャーナルはパスの大文字小文字を無視した辞書順で載る
    /// </summary>
    /// <remarks>
    /// <para>前提: 生きているトランザクションがあり、未コミットの Add 残骸を辞書順の逆に 2 件書いてある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: Journals はパスの大文字小文字を無視した辞書順であり、どちらも RolledBack であり、生きているトランザクションは一覧に無い</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_複数ジャーナルはパスの大文字小文字を無視した辞書順で載ること()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using ITransaction live = await global::Txfio.Txfio.BeginAsync(work.Path);
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string liveJournal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(MetadataNames.TryGetTransactionId(liveJournal, out Guid liveId));
        Guid later = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        Guid earlier = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "z.txt", "z", later);
        await LeftoverAddFiles.WriteAddAsync(work.Path, committing: false, "a.txt", "a", earlier);

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.RolledBack, report.Result);
        Assert.Equal(2, report.Journals.Count);
        Assert.Equal(earlier, report.Journals[0].TransactionId);
        Assert.Equal(RecoverResult.RolledBack, report.Journals[0].Result);
        Assert.Equal(later, report.Journals[1].TransactionId);
        Assert.Equal(RecoverResult.RolledBack, report.Journals[1].Result);
        Assert.DoesNotContain(report.Journals, journal => journal.TransactionId == liveId);
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(work.Path, "z.txt")));
        Assert.Empty(live.GetPendingChanges());
    }

    /// <summary>
    /// ファイル名から ID を取れない、読めないジャーナルは、空の ID で一覧に入れない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイル名が tx-*.journal に合うが GUID ではない、壊れたジャーナルがある</para>
    /// <para>手順: RecoverAsync する</para>
    /// <para>期待: 全体は JournalUnreadable、一覧は空、ジャーナルは残る</para>
    /// </remarks>
    [Fact]
    public async Task RecoverAsync_IDを取れない読めないジャーナルは一覧に入れないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        Directory.CreateDirectory(metadata);
        string journal = System.IO.Path.Combine(metadata, "tx-not-a-guid.journal");
        await File.WriteAllTextAsync(journal, "{\"version\":1,\"transac");

        RecoverReport report = await global::Txfio.Txfio.RecoverAsync(work.Path);

        Assert.Equal(RecoverResult.JournalUnreadable, report.Result);
        Assert.Empty(report.Journals);
        Assert.True(File.Exists(journal));
    }
}
