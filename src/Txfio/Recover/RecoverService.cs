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
    /// <returns>復旧結果</returns>
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
            return await RecoverJournalsAsync(journals, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sentinel.Release();
        }
    }

    private static async Task<RecoverResult> RecoverJournalsAsync(string[] journals, CancellationToken cancellationToken)
    {
        bool rolledBack = false;
        bool rolledForward = false;
        bool conflictDetected = false;
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

                JournalDocument? document = await JournalStore.TryReadAsync(journalPath, cancellationToken)
                    .ConfigureAwait(false);

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

                if (document is not null)
                {
                    StagingApplier.DeleteCreateDirectoryTrees(document.Operations);
                    StagingApplier.DeleteStagingFiles(document.Operations);
                }

                await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
                rolledBack = true;
            }
            finally
            {
                await liveness.DisposeAsync().ConfigureAwait(false);
            }
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
