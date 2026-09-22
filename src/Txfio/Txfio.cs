namespace Txfio;

/// <summary>
/// トランザクショナルなファイルIOのエントリポイント
/// </summary>
public static class Txfio
{
    /// <summary>
    /// ワークフォルダに対するトランザクションを開始する
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="cancellationToken">開始処理を取り消すトークン</param>
    /// <returns>開始したトランザクション</returns>
    /// <exception cref="DirectoryNotFoundException">ワークフォルダが存在しない</exception>
    public static async Task<ITransaction> BeginAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new DirectoryNotFoundException("ワークフォルダが存在しません: " + workFolder);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureMetadataFolder(workFolder);

        Guid transactionId = Guid.NewGuid();
        string journalPath = MetadataNames.JournalPath(workFolder, transactionId);
        await JournalStore.WriteNewAsync(journalPath, transactionId, cancellationToken).ConfigureAwait(false);
        return new Transaction(journalPath);
    }

    /// <summary>
    /// 未完了のトランザクションを検出し、ロールバックまたはロールフォワードする
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>復旧結果</returns>
    /// <exception cref="DirectoryNotFoundException">ワークフォルダが存在しない</exception>
    public static async Task<RecoverResult> RecoverAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new DirectoryNotFoundException("ワークフォルダが存在しません: " + workFolder);
        }

        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return RecoverResult.NoPendingTransactions;
        }

        string[] journals = Directory.GetFiles(metadataFolder, MetadataNames.JournalSearchPattern, SearchOption.TopDirectoryOnly);
        if (journals.Length == 0)
        {
            return RecoverResult.NoPendingTransactions;
        }

        bool rolledBack = false;
        foreach (string journalPath in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JournalDocument? document = await JournalStore.TryReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
            if (document is { Committing: true })
            {
                continue;
            }

            await JournalStore.DeleteAsync(journalPath).ConfigureAwait(false);
            rolledBack = true;
        }

        return rolledBack ? RecoverResult.RolledBack : RecoverResult.NoPendingTransactions;
    }

    private static void EnsureMetadataFolder(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        DirectoryInfo directory = Directory.CreateDirectory(metadataFolder);
        directory.Attributes |= FileAttributes.Hidden;
    }
}
