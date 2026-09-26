using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Txfio;

/// <summary>
/// トランザクションの公開契約
/// </summary>
/// <remarks>
/// 公開メンバーは重なって呼べず、重なった呼び出しは状態を変える前に <see cref="InvalidOperationException"/> になる
/// </remarks>
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
    /// <exception cref="ExternalConflictException">対象が既にあり、ファイル Move の移動元ではない、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、ディレクトリ Move の移動元への追加、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、配下にこのトランザクションの操作がある、ディレクトリ Move の配下である、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同一ボリューム内のファイルまたはディレクトリの移動を予約する
    /// </summary>
    /// <param name="oldPath">移動元パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="newPath">移動先パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">移動元が無い、移動先が別の Move の移動元でもなく塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが移動元、移動先、またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ボリュームをまたぐ移動である</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、同じパスへの移動（大文字小文字だけの違いを含む）、別操作でステージング済み、空いている端が無い移動、削除予約済みディレクトリへの移動、移動元または移動先の配下への操作、自分自身の配下への移動、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同一ボリューム内のファイルまたはディレクトリの移動を予約する
    /// <paramref name="overwrite"/> が <see langword="true"/> なら、移動先の既存ファイルを置き換える
    /// 移動元がディレクトリのときは、移動先の既存ディレクトリを中身ごと入れ替える
    /// </summary>
    /// <remarks>
    /// ファイルの置き換えはコミットで 1 回の rename（`MOVEFILE_REPLACE_EXISTING`）であり、バイトはコピーしない
    /// ディレクトリの入れ替えは、移動先を `{名前}.{txid}.txold` へ退け、移動元を移動先へ rename してから `.txold` を消す
    /// 移動先にこのトランザクションのファイルの Delete があれば、その Delete をファイルの置き換えに畳む
    /// 移動先の DeleteTree は、ディレクトリの入れ替えに畳む
    /// ファイルの置き換えとディレクトリの入れ替えでは、移動元と移動先へはこのあと続けて操作できない
    /// ディレクトリの入れ替えでは、その配下へも続けて操作できない
    /// </remarks>
    /// <param name="oldPath">移動元パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="newPath">移動先パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="overwrite"><see langword="true"/> なら移動先の既存ファイルを置き換え、移動元がディレクトリなら既存ディレクトリを中身ごと入れ替える（<see langword="false"/> は <see cref="MoveAsync(string, string, CancellationToken)"/> と同じ）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>予約の完了</returns>
    /// <exception cref="ExternalConflictException">移動元が無い、ファイルとディレクトリを入れ替えようとしている、置き換えないのに移動先が塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが移動元、移動先、またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ボリュームをまたぐ移動である</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、同じパスへの移動、別操作でステージング済み、置き換えの Move の移動元か移動先への操作、空いている端が無い移動、削除予約済みディレクトリへの移動、移動元または移動先の配下への操作、自分自身の配下への移動、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task MoveAsync(string oldPath, string newPath, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>
    /// 空ディレクトリを呼び出した時点で作る（配下では通常の操作ができ、中身は素のファイル API でも書ける）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>作成の完了</returns>
    /// <exception cref="ExternalConflictException">対象が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、配下に操作がある、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、同じパスへのコピー、自分自身の配下へのコピー、シンボリックリンク、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、自分自身の配下への取り込み、シンボリックリンク、リパースポイント、またはメタデータ配下である</exception>
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
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、シンボリックリンク、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">コピー元がワークフォルダの外、またはコピー先がワークフォルダの中である</exception>
    Task ExportAsync(
        string path,
        string externalPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダ内のファイルまたはディレクトリから ZIP を作り、ワークフォルダ内に Add する
    /// </summary>
    /// <param name="source">入力（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="archivePath">作る ZIP のパス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="compressionLevel">圧縮レベル</param>
    /// <param name="includeBaseDirectory">入力がディレクトリのとき、その名前をエントリのルートに含めるか</param>
    /// <param name="progress">読み込んだ圧縮前のバイト数（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">入力が無い、ZIP のパスが既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが入力、ZIP のパス、またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、入力の配下に操作がある、ZIP のパスが入力の配下、シンボリックリンク、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task CreateArchiveAsync(
        string source,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定したファイルとディレクトリを指定した名前で入れた ZIP を作り、ワークフォルダ内に Add する
    /// </summary>
    /// <param name="entries">入れるものと ZIP の中での名前の組（リストの順に入れる）</param>
    /// <param name="archivePath">作る ZIP のパス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="compressionLevel">圧縮レベル</param>
    /// <param name="progress">読み込んだ圧縮前のバイト数（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException">組の列、要素、または要素のパスが null である</exception>
    /// <exception cref="ExternalConflictException">入力が無い、ZIP のパスが既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが入力、ZIP のパス、またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、入力がステージング済み、入力の配下に操作がある、ZIP のパスが入力の配下、シンボリックリンク、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外、またはエントリ名が不正、重複、ファイルとディレクトリの同名である</exception>
    Task CreateArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダ内のファイルまたはディレクトリから、ワークフォルダの外に ZIP を作る
    /// </summary>
    /// <param name="source">入力（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="externalArchivePath">ワークフォルダの外に作る ZIP のパス</param>
    /// <param name="compressionLevel">圧縮レベル</param>
    /// <param name="includeBaseDirectory">入力がディレクトリのとき、その名前をエントリのルートに含めるか</param>
    /// <param name="progress">読み込んだ圧縮前のバイト数（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き出しの完了</returns>
    /// <exception cref="ExternalConflictException">入力が無い、ZIP のパスが塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、シンボリックリンク、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">入力がワークフォルダの外、または ZIP のパスがワークフォルダの中である</exception>
    Task ExportArchiveAsync(
        string source,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定したファイルとディレクトリを指定した名前で入れた ZIP を、ワークフォルダの外に作る
    /// </summary>
    /// <param name="entries">入れるものと ZIP の中での名前の組（リストの順に入れる）</param>
    /// <param name="externalArchivePath">ワークフォルダの外に作る ZIP のパス</param>
    /// <param name="compressionLevel">圧縮レベル</param>
    /// <param name="progress">読み込んだ圧縮前のバイト数（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>書き出しの完了</returns>
    /// <exception cref="ArgumentNullException">組の列、要素、または要素のパスが null である</exception>
    /// <exception cref="ExternalConflictException">入力が無い、ZIP のパスが塞がっている、または親ディレクトリが無い</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、シンボリックリンク、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">入力がワークフォルダの外、ZIP のパスがワークフォルダの中、またはエントリ名が不正、重複、ファイルとディレクトリの同名である</exception>
    Task ExportArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダ内の ZIP を、ワークフォルダ内の新しいディレクトリへ展開し、各ファイルを Add する
    /// </summary>
    /// <param name="archivePath">展開する ZIP（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パスとし、ステージング済みならその内容を読む）</param>
    /// <param name="destinationDir">展開先の新しいディレクトリ（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="entryNameEncoding">UTF-8 フラグの無いエントリ名の読み方（null のときは .NET の既定）</param>
    /// <param name="maxExtractedBytes">展開後のバイト数の合計の上限（null のときは上限なし、外から受け取った ZIP では渡す）</param>
    /// <param name="progress">展開後のバイト数と、エントリの合計サイズ（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">ZIP が無い、展開先が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが展開先またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ZIP のパスがディレクトリである</exception>
    /// <exception cref="InvalidDataException">展開先の外へ出る名前がある、Windows で使えない名前がある、`.txnew` で終わる名前がある、名前が重複している、ファイルとディレクトリの同名がある、または申告した展開後のサイズの合計か実際に読んだバイト数が <paramref name="maxExtractedBytes"/> を超える（合計が long に収まらないとき、ファイルの Length が 0 未満のとき、ZIP 自体が読めないときも同じ）</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExtractedBytes"/> が 0 未満である</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、展開先の配下に操作がある、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task ExtractArchiveAsync(
        string archivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ワークフォルダの外の ZIP を、ワークフォルダ内の新しいディレクトリへ展開し、各ファイルを Add する
    /// </summary>
    /// <param name="externalArchivePath">ワークフォルダの外にある ZIP</param>
    /// <param name="destinationDir">展開先の新しいディレクトリ（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="entryNameEncoding">UTF-8 フラグの無いエントリ名の読み方（null のときは .NET の既定）</param>
    /// <param name="maxExtractedBytes">展開後のバイト数の合計の上限（null のときは上限なし、外から受け取った ZIP では渡す）</param>
    /// <param name="progress">展開後のバイト数と、エントリの合計サイズ（null のときは通知しない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">ZIP が無い、展開先が既にある、または親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが展開先またはワークフォルダを押さえている</exception>
    /// <exception cref="UnsupportedOperationException">ZIP のパスがディレクトリである</exception>
    /// <exception cref="InvalidDataException">展開先の外へ出る名前がある、Windows で使えない名前がある、`.txnew` で終わる名前がある、名前が重複している、ファイルとディレクトリの同名がある、または申告した展開後のサイズの合計か実際に読んだバイト数が <paramref name="maxExtractedBytes"/> を超える（合計が long に収まらないとき、ファイルの Length が 0 未満のとき、ZIP 自体が読めないときも同じ）</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExtractedBytes"/> が 0 未満である</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、展開先の配下に操作がある、リパースポイント、またはメタデータ配下である</exception>
    /// <exception cref="ArgumentException">ZIP のパスがワークフォルダの中、または展開先がワークフォルダの外である</exception>
    Task ImportArchiveAsync(
        string externalArchivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// コミット後の姿のファイルを開く
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">呼び出し開始時のみ有効な取り消しトークン</param>
    /// <returns>位置 0 の読み取りストリーム（呼び出し側が破棄する）</returns>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// コミット後の姿で、ファイルかディレクトリがあるかどうかを返す
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">呼び出し開始時のみ有効な取り消しトークン</param>
    /// <returns>ファイルかディレクトリがあるなら <see langword="true"/></returns>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取ったバイトを文字列にする（エンコーディングは BOM を見て決める）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読み取った文字列</returns>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取ったバイトを、指定したエンコーディングで文字列にする（BOM があればそちらを優先する）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="encoding">BOM が無いときのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>読み取った文字列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<string> ReadAllTextAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取ったバイトを行の配列にする（エンコーディングは BOM を見て決める）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>改行を含まない行の配列</returns>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取ったバイトを、指定したエンコーディングで行の配列にする（BOM があればそちらを優先する）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="encoding">BOM が無いときのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>改行を含まない行の配列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<string[]> ReadAllLinesAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 文字列を書く（コミット後の姿でファイルが無ければ Add、あれば Update、エンコーディングは BOM なし UTF-8）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む文字列（null は空文字列として書く）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task WriteAllTextAsync(
        string path,
        string? contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 文字列を、指定したエンコーディングで書く（コミット後の姿でファイルが無ければ Add、あれば Update）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む文字列（null は空文字列として書く）</param>
    /// <param name="encoding">書き込みのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task WriteAllTextAsync(
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 行を書く（コミット後の姿でファイルが無ければ Add、あれば Update、エンコーディングは BOM なし UTF-8）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む行（各行の改行は含めない）</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 行を、指定したエンコーディングで書く（コミット後の姿でファイルが無ければ Add、あれば Update）
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="contents">書き込む行（各行の改行は含めない）</param>
    /// <param name="encoding">書き込みのエンコーディング</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> または <paramref name="encoding"/> が null</exception>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取ったバイトを JSON として読む
    /// </summary>
    /// <typeparam name="T">読み取る型</typeparam>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="options">省略時は <see cref="JsonSerializer"/> の既定</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>デシリアライズした値</returns>
    /// <exception cref="JsonException">JSON として読めない</exception>
    /// <exception cref="ExternalConflictException">対象が無い</exception>
    /// <exception cref="UnsupportedOperationException">対象がディレクトリである</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task<T?> ReadFromJsonAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 値を JSON にして書く（コミット後の姿でファイルが無ければ Add、あれば Update）
    /// </summary>
    /// <typeparam name="T">書き込む型</typeparam>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <param name="value">書き込む値</param>
    /// <param name="options">省略時は <see cref="JsonSerializer"/> の既定</param>
    /// <param name="cancellationToken">取り消し用のトークン</param>
    /// <returns>ステージングの完了</returns>
    /// <exception cref="ExternalConflictException">親ディレクトリが無い</exception>
    /// <exception cref="LockContentionException">他のトランザクションが対象またはワークフォルダを押さえている</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、別操作でステージング済み、リパースポイント、メタデータ配下、またはコミット済みである</exception>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    Task WriteAsJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ステージングした変更をワークフォルダへ確定する
    /// </summary>
    /// <param name="cancellationToken">コミット開始前まで有効な取り消しトークン</param>
    /// <returns>全体の結果と、拒んだ操作または飛ばした操作</returns>
    /// <exception cref="RecoveryRequiredException">持ち主のいない残骸ジャーナルが残っている（未コミットのまま残る）</exception>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている、またはコミット済みである</exception>
    Task<CommitReport> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 現在のジャーナル上の未確定操作一覧を返す
    /// </summary>
    /// <returns>操作一覧（この時点では空になりうる）</returns>
    /// <exception cref="InvalidOperationException">呼び出しが重なっている</exception>
    IReadOnlyList<PendingChange> GetPendingChanges();
}
