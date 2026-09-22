namespace Txfio;

/// <summary>
/// トランザクションの公開契約
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>
    /// 新規ファイルの内容をステージングする
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="content">書き込む内容（呼び出し側が所有する）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    Task AddAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存ファイルを新しい内容でステージングする
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="content">書き込む内容（呼び出し側が所有する）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    Task UpdateAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存のファイルまたはディレクトリの削除を予約する
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同一ボリューム内のファイル移動を予約する
    /// </summary>
    /// <param name="oldPath">移動元パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="newPath">移動先パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 外部が作った既存ファイルをトランザクションに取り込む
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>取り込みの完了</returns>
    Task AttachAsync(string path, CancellationToken cancellationToken = default);

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
