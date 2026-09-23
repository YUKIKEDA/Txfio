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
    /// <exception cref="ExternalConflictException">対象が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象を押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task AddAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存ファイルを新しい内容でステージングする
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="content">書き込む内容（呼び出し側が所有する）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">対象が無い、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象を押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task UpdateAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存のファイルまたはディレクトリの削除を予約する
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">対象が無い、またはディレクトリ直下に予定外の子がある</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象を押さえている</exception>
    /// <exception cref="InvalidOperationException">メタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同一ボリューム内のファイル移動を予約する
    /// </summary>
    /// <param name="oldPath">移動元パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="newPath">移動先パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">移動元が無い、移動先が塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが移動元または移動先を押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ディレクトリの移動、またはボリュームをまたぐ移動である</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、削除予約済みディレクトリへの移動、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 外部が作った既存ファイルをトランザクションに取り込む
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>取り込みの完了</returns>
    /// <exception cref="ExternalConflictException">対象が無い、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象を押さえている</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
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
