namespace Txfio;

/// <summary>
/// トランザクションの公開契約
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>
    /// ステージングした変更をワークフォルダへ確定する
    /// </summary>
    /// <param name="cancellationToken">コミット開始前まで有効な取り消しトークン</param>
    /// <returns>確定結果</returns>
    Task<CommitResult> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 現在のジャーナル上の未確定操作一覧を返す
    /// </summary>
    /// <returns>操作一覧（この時点では空になりうる）</returns>
    IReadOnlyList<PendingChange> GetPendingChanges();
}
