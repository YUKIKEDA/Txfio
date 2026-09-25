namespace Txfio;

/// <summary>
/// トランザクショナルなファイルIOのエントリポイント
/// </summary>
public static class Txfio
{
    /// <summary>
    /// ワークフォルダに対するトランザクションを開始する（ロックの待ちはゼロ）
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="cancellationToken">開始処理を取り消すトークン</param>
    /// <returns>開始したトランザクション</returns>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている</exception>
    public static Task<ITransaction> BeginAsync(string path, CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, TimeSpan.Zero, detectExternalChanges: false, cancellationToken);
    }

    /// <summary>
    /// ワークフォルダに対するトランザクションを開始する（ロックの待ちはゼロ）
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="detectExternalChanges"><see langword="true"/> のとき、ステージ後に記録と違う Update をコミット前に失敗にする</param>
    /// <param name="cancellationToken">開始処理を取り消すトークン</param>
    /// <returns>開始したトランザクション</returns>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている</exception>
    public static Task<ITransaction> BeginAsync(
        string path,
        bool detectExternalChanges,
        CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, TimeSpan.Zero, detectExternalChanges, cancellationToken);
    }

    /// <summary>
    /// ワークフォルダに対するトランザクションを開始する
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="lockWait">ロックが取れないとき、公開メソッド 1 回ごとに待つ上限（ゼロは待たない）</param>
    /// <param name="cancellationToken">開始処理を取り消すトークン</param>
    /// <returns>開始したトランザクション</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> がゼロ未満である（<see cref="Timeout.InfiniteTimeSpan"/> は除く）</exception>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている</exception>
    public static Task<ITransaction> BeginAsync(string path, TimeSpan lockWait, CancellationToken cancellationToken = default)
    {
        return BeginAsync(path, lockWait, detectExternalChanges: false, cancellationToken);
    }

    /// <summary>
    /// ワークフォルダに対するトランザクションを開始する
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="lockWait">ロックが取れないとき、公開メソッド 1 回ごとに待つ上限（ゼロは待たない）</param>
    /// <param name="detectExternalChanges"><see langword="true"/> のとき、ステージ後に記録と違う Update をコミット前に失敗にする</param>
    /// <param name="cancellationToken">開始処理を取り消すトークン</param>
    /// <returns>開始したトランザクション</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> がゼロ未満である（<see cref="Timeout.InfiniteTimeSpan"/> は除く）</exception>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている</exception>
    public static async Task<ITransaction> BeginAsync(
        string path,
        TimeSpan lockWait,
        bool detectExternalChanges,
        CancellationToken cancellationToken = default)
    {
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = NormalizeWorkFolder(path);

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

        return new Transaction(workFolder, transactionId, journalPath, liveness, lockWait, detectExternalChanges);
    }

    /// <summary>
    /// 未完了のトランザクションを検出し、ロールバックまたはロールフォワードする（ワークフォルダ全体のロックの待ちはゼロ）
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>全体の結果と、処理したジャーナル（JSON として読めないジャーナルがあれば <see cref="RecoverResult.JournalUnreadable"/>）</returns>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="LockContentionException">他のトランザクションがワークフォルダを押さえている</exception>
    /// <exception cref="IOException">ジャーナルの読み取りに失敗した（そのジャーナルは残る）</exception>
    public static Task<RecoverReport> RecoverAsync(string path, CancellationToken cancellationToken = default)
    {
        return RecoverAsync(path, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// 未完了のトランザクションを検出し、ロールバックまたはロールフォワードする
    /// </summary>
    /// <param name="path">既存のワークフォルダ</param>
    /// <param name="lockWait">ワークフォルダ全体のロックが取れないとき、この呼び出しで待つ上限（ゼロは待たない）</param>
    /// <param name="cancellationToken">検出と復旧を取り消すトークン</param>
    /// <returns>全体の結果と、処理したジャーナル（JSON として読めないジャーナルがあれば <see cref="RecoverResult.JournalUnreadable"/>）</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> がゼロ未満である（<see cref="Timeout.InfiniteTimeSpan"/> は除く）</exception>
    /// <exception cref="ExternalConflictException">ワークフォルダが存在しない</exception>
    /// <exception cref="IOException">ワークフォルダの長い名前を取れない</exception>
    /// <exception cref="LockContentionException">期限までにワークフォルダを押さえられない</exception>
    /// <exception cref="OperationCanceledException">ワークフォルダ全体のロックを待っているあいだに取り消された</exception>
    /// <exception cref="IOException">ジャーナルの読み取りに失敗した（そのジャーナルは残る）</exception>
    public static async Task<RecoverReport> RecoverAsync(string path, TimeSpan lockWait, CancellationToken cancellationToken = default)
    {
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string workFolder = NormalizeWorkFolder(path);

        return await RecoverService.RecoverAsync(workFolder, lockWait, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeWorkFolder(string path)
    {
        string workFolder = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(workFolder))
        {
            throw new ExternalConflictException("ワークフォルダが存在しません: " + workFolder, workFolder);
        }

        return WorkPath.ToLongPath(workFolder);
    }

    private static void EnsureMetadataFolder(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        DirectoryInfo directory = Directory.CreateDirectory(metadataFolder);
        directory.Attributes |= FileAttributes.Hidden;
    }
}
