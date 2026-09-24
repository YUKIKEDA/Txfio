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
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">対象が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task AddAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存ファイルを新しい内容でステージングする
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="content">書き込む内容（呼び出し側が所有する）</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">対象が無い、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task UpdateAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存のファイルまたはディレクトリの削除を予約する
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">対象が無い、またはディレクトリ直下に予定外の子がある</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">メタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// ディレクトリとその配下すべての削除を予約する
    /// </summary>
    /// <param name="path">対象ディレクトリ（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">対象ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">対象がファイルである</exception>
    /// <exception cref="InvalidOperationException">配下にこのトランザクションの操作がある、ディレクトリ Move の配下である、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同一ボリューム内のファイルまたはディレクトリの移動を予約する
    /// </summary>
    /// <param name="oldPath">移動元パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="newPath">移動先パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">移動元が無い、移動先が塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが移動元、移動先、またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ボリュームをまたぐ移動である</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、削除予約済みディレクトリへの移動、移動元または移動先の配下への操作、自分自身の配下への移動、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 空ディレクトリを呼び出した時点で作る。配下では通常の操作ができ、中身は素のファイル API でも書ける
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>作成の完了</returns>
    /// <exception cref="ExternalConflictException">対象が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、配下に操作がある、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダ内のファイルまたはディレクトリをコピーする
    /// </summary>
    /// <param name="source">コピー元（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="destination">コピー先（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">コピー元が無い、コピー先が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションがコピー元、コピー先、またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、同じパスへのコピー、自分自身の配下へのコピー、シンボリックリンク、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task CopyAsync(
        string source,
        string destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダの外にあるファイルまたはディレクトリをコピーして Add する
    /// </summary>
    /// <param name="externalPath">ワークフォルダの外にあるコピー元</param>
    /// <param name="targetPath">コピー先（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">コピー元が無い、コピー先が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションがコピー先またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">別操作でステージング済み、自分自身の配下への取り込み、シンボリックリンク、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">コピー元がワークフォルダの中、またはコピー先がワークフォルダの外である</exception>
    Task ImportAsync(
        string externalPath,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダのファイルまたはディレクトリを、ワークフォルダの外へコピーする
    /// </summary>
    /// <param name="path">コピー元（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="externalPath">ワークフォルダの外にあるコピー先</param>
    /// <param name="progress">コピーの進み具合（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>コピーの完了</returns>
    /// <exception cref="ExternalConflictException">コピー元が無い、コピー先が塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">シンボリックリンク、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">コピー元がワークフォルダの外、またはコピー先がワークフォルダの中である</exception>
    Task ExportAsync(
        string path,
        string externalPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ステージング済みならその内容を、無ければ本物のファイルを開く
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">呼び出し開始時のみ有効な取り消しトークン</param>
    /// <returns>位置 0 の読み取りストリーム（呼び出し側が破棄する）</returns>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">メタデータ配下である、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);

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
