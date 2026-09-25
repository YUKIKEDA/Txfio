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
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている</exception>
    public static async Task<ITransaction> BeginAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new ExternalConflictException("ワークフォルダが存在しません: " + workFolder, workFolder);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureMetadataFolder(workFolder);
        StaleJournals.ThrowIfAny(workFolder);

        Guid transactionId = Guid.NewGuid();
        string journalPath = MetadataNames.JournalPath(workFolder, transactionId);

        // Recover が生きているトランザクションのジャーナルを見つけたとき、必ず共有違反になるよう先に開く
        FileStream liveness = LivenessLock.Create(MetadataNames.LivenessLockPath(journalPath));
        try
        {
            await JournalStore.WriteNewAsync(journalPath, transactionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await liveness.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Transaction(workFolder, transactionId, journalPath, liveness);
    }

    /// <summary>
    /// 未完了のトランザクションを検出し、ロールバックまたはロールフォワードする
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>全体の結果と、処理したジャーナル（JSON として読めないジャーナルがあれば <see cref="RecoverResult.JournalUnreadable"/>）</returns>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="LockContentionException">他のトランザクションがワークフォルダを押さえている</exception>
    /// <exception cref="IOException">ジャーナルの読み取りに失敗した（そのジャーナルは残る）</exception>
    public static async Task<RecoverReport> RecoverAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new ExternalConflictException("ワークフォルダが存在しません: " + workFolder, workFolder);
        }

        return await RecoverService.RecoverAsync(workFolder, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureMetadataFolder(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        DirectoryInfo directory = Directory.CreateDirectory(metadataFolder);
        directory.Attributes |= FileAttributes.Hidden;
    }
}
