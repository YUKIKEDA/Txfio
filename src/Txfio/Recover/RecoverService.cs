namespace Txfio;

/// <summary>
/// 未完了ジャーナルのロールバックとロールフォワード
/// </summary>
internal static class RecoverService
{
    /// <summary>
    /// ワークフォルダ内の残骸ジャーナルを、パスの大文字小文字を無視した辞書順で処理する
    /// </summary>
    /// <param name="workFolder">既存のワークフォルダ</param>
    /// <param name="lockWait">ワークフォルダ全体のロックが取れないとき、この呼び出しで待つ上限</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>全体の結果と、処理したジャーナル（パスの大文字小文字を無視した辞書順であり、JSON として読めないジャーナルがあれば <see cref="RecoverResult.JournalUnreadable"/>）</returns>
    /// <exception cref="IOException">ジャーナルの読み取りに失敗した（そのジャーナルは残る）</exception>
    /// <exception cref="LockContentionException">期限までにワークフォルダ全体のロックを取れない</exception>
    /// <exception cref="OperationCanceledException">ワークフォルダ全体のロックを待っているあいだに取り消された</exception>
    internal static async Task<RecoverReport> RecoverAsync(
        string workFolder,
        TimeSpan lockWait,
        CancellationToken cancellationToken)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return new RecoverReport(RecoverResult.NoPendingTransactions, Array.Empty<JournalReport>());
        }

        // 処理中に別のトランザクションが確定したデータを、ロールフォワードやロールバックで消さない
        PathLockSet sentinel = new PathLockSet();
        try
        {
            sentinel.BeginAttempt(lockWait, cancellationToken);
            await sentinel.AcquireExclusiveAsync(workFolder).ConfigureAwait(false);
            await sentinel.RejectForeignLocksAsync(workFolder).ConfigureAwait(false);
            string[] journals = Directory.GetFiles(
                metadataFolder,
                MetadataNames.JournalSearchPattern,
                SearchOption.TopDirectoryOnly);

            // パスの大文字小文字を無視した辞書順（報告と、読み取り失敗より前に確定する範囲を毎回同じにする）
            Array.Sort(journals, static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            RecoverReport report = await RecoverJournalsAsync(workFolder, journals, cancellationToken).ConfigureAwait(false);
            DeleteOrphanJournalTemps(metadataFolder);
            return report;
        }
        finally
        {
            sentinel.Release();
        }
    }

    // 初回のジャーナルを rename する前に落ちると、一時ファイルだけが残る（持ち主が生きていれば触らない）
    private static void DeleteOrphanJournalTemps(string metadataFolder)
    {
        string[] temps = Directory.GetFiles(
            metadataFolder,
            MetadataNames.JournalTempSearchPattern,
            SearchOption.TopDirectoryOnly);
        foreach (string tempPath in temps)
        {
            if (!MetadataNames.TryGetJournalPathFromTemp(tempPath, out string journalPath)
                || File.Exists(journalPath))
            {
                continue;
            }

            using FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is not null && !File.Exists(journalPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<RecoverReport> RecoverJournalsAsync(
        string workFolder,
        string[] journals,
        CancellationToken cancellationToken)
    {
        bool rolledBack = false;
        bool rolledForward = false;
        bool conflictDetected = false;
        bool journalUnreadable = false;
        List<JournalReport> reports = new List<JournalReport>();
        foreach (string journalPath in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 開けなければ持ち主が生きているので、ジャーナルにも残骸にも触れない
            FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is null)
            {
                continue;
            }

            try
            {
                // 一覧のあと持ち主が正常に終わっていれば、何もしない
                if (!File.Exists(journalPath))
                {
                    continue;
                }

                // 落ちた上書きの一時ファイルは、読む前に消す
                JournalStore.DeleteTemp(journalPath);
                JournalDocument? document = await JournalStore.TryReadAsync(journalPath, cancellationToken)
                    .ConfigureAwait(false);

                if (document is null)
                {
                    // 作ったディレクトリは文書が読めないので特定できず、ファイル名から取れた ID の .txnew と再ステージの退避だけ消す
                    if (MetadataNames.TryGetTransactionId(journalPath, out Guid transactionId))
                    {
                        StagingApplier.DeleteStagingFiles(workFolder, transactionId);
                        reports.Add(new JournalReport(
                            transactionId,
                            RecoverResult.JournalUnreadable,
                            Array.Empty<OperationReport>()));
                    }

                    journalUnreadable = true;
                    continue;
                }

                if (document is { Committing: true })
                {
                    bool appliedAll = StagingApplier.TryApplyAll(
                        document.Operations,
                        NoFaultInjector.Instance,
                        out OperationReport[] skipped);
                    IReadOnlyList<OperationReport> operations = Array.Empty<OperationReport>();
                    RecoverResult journalResult = RecoverResult.RolledForward;
                    if (!appliedAll)
                    {
                        // 結果は 1 回だけ返し、次の Recover でやり直さない
                        StagingApplier.DeleteStagingFiles(document.Operations);
                        operations = skipped;
                        journalResult = RecoverResult.ConflictDetected;
                        conflictDetected = true;
                    }

                    await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                    reports.Add(new JournalReport(document.TransactionId, journalResult, operations));
                    rolledForward |= appliedAll;
                    continue;
                }

                StagingApplier.DeleteCreateDirectoryTrees(document.Operations);
                StagingApplier.DeleteStagingFiles(document.Operations);
                StagingApplier.DeleteStagingBackups(document.Operations);
                StagingApplier.DeleteCreatedDirectories(document.CreatedDirectories);
                await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                reports.Add(new JournalReport(
                    document.TransactionId,
                    RecoverResult.RolledBack,
                    Array.Empty<OperationReport>()));
                rolledBack = true;
            }
            finally
            {
                await liveness.DisposeAsync().ConfigureAwait(false);
            }
        }

        RecoverResult result = RecoverResult.NoPendingTransactions;
        if (journalUnreadable)
        {
            result = RecoverResult.JournalUnreadable;
        }
        else if (conflictDetected)
        {
            result = RecoverResult.ConflictDetected;
        }
        else if (rolledForward)
        {
            result = RecoverResult.RolledForward;
        }
        else if (rolledBack)
        {
            result = RecoverResult.RolledBack;
        }

        return new RecoverReport(result, reports);
    }
}
