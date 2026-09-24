namespace Txfio;

/// <summary>
/// 未完了ジャーナルのロールバックとロールフォワード
/// </summary>
internal static class RecoverService
{
    /// <summary>
    /// ワークフォルダ内の残骸ジャーナルを処理する
    /// </summary>
    /// <param name="workFolder">既存のワークフォルダ</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>復旧結果。JSON として読めないジャーナルがあれば <see cref="RecoverResult.JournalUnreadable"/></returns>
    /// <exception cref="IOException">ジャーナルの読み取りに失敗した（そのジャーナルは残る）</exception>
    internal static async Task<RecoverResult> RecoverAsync(string workFolder, CancellationToken cancellationToken)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return RecoverResult.NoPendingTransactions;
        }

        // 処理中に別のトランザクションが確定したデータを、ロールフォワードやロールバックで消さない
        PathLockSet sentinel = new PathLockSet();
        try
        {
            sentinel.AcquireExclusive(workFolder);
            sentinel.RejectForeignLocks(workFolder);
            string[] journals = Directory.GetFiles(
                metadataFolder,
                MetadataNames.JournalSearchPattern,
                SearchOption.TopDirectoryOnly);
            return await RecoverJournalsAsync(workFolder, journals, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sentinel.Release();
        }
    }

    private static async Task<RecoverResult> RecoverJournalsAsync(
        string workFolder,
        string[] journals,
        CancellationToken cancellationToken)
    {
        bool rolledBack = false;
        bool rolledForward = false;
        bool conflictDetected = false;
        bool journalUnreadable = false;
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
                    // 文書が読めないので作ったディレクトリは特定できない。そのトランザクション ID の .txnew だけ消す
                    if (MetadataNames.TryGetTransactionId(journalPath, out Guid transactionId))
                    {
                        StagingApplier.DeleteStagingFiles(workFolder, transactionId);
                    }

                    journalUnreadable = true;
                    continue;
                }

                if (document is { Committing: true })
                {
                    bool appliedAll = StagingApplier.TryApplyAll(document.Operations);
                    if (!appliedAll)
                    {
                        // 結果は 1 回だけ返し、次の Recover でやり直さない
                        StagingApplier.DeleteStagingFiles(document.Operations);
                        conflictDetected = true;
                    }

                    await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                    rolledForward |= appliedAll;
                    continue;
                }

                StagingApplier.DeleteCreateDirectoryTrees(document.Operations);
                StagingApplier.DeleteStagingFiles(document.Operations);
                StagingApplier.DeleteStagingBackups(workFolder, document.TransactionId);
                StagingApplier.DeleteCreatedDirectories(document.CreatedDirectories);
                await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                rolledBack = true;
            }
            finally
            {
                await liveness.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (journalUnreadable)
        {
            return RecoverResult.JournalUnreadable;
        }

        if (conflictDetected)
        {
            return RecoverResult.ConflictDetected;
        }

        if (rolledForward)
        {
            return RecoverResult.RolledForward;
        }

        return rolledBack ? RecoverResult.RolledBack : RecoverResult.NoPendingTransactions;
    }
}
