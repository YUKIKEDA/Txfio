# Txfio 設計ドキュメント

2026-09-24

実装の順序は [`roadmap.md`](roadmap.md) を見ること。Phase 境界は仮であり、変えてよい。

## 概要

C# で、ファイルサーバーなど IO が遅い環境でも動く、git のステージング／コミットに着想を得たトランザクショナルなファイル IO ライブラリである。

既存の `TxFileManager` はジャーナルが揮発するため却下した。

**非機能要件**

- ファイルサーバー等のIOが遅い環境でスムーズに動作すること。無駄なコピー・無駄なIOを避け、IOコストを最小化する
- 一時フォルダ（ワークフォルダ外の別置き場所）を使わない
- デスクトップ環境での利用を前提とし、通常のファイルIOから見えてしまう「ダーティリード」は許容する（複数プロセスにまたがる完全な分離性は追求しない）

**対象プラットフォーム**: Windows専用（NTFS/SMBファイルサーバー想定）からスタートし、将来のクロスプラットフォーム拡張の余地は残す。パッケージの TFM は `net8.0` 単一（`net8.0-windows` にはしない）。ランタイムとして保証するのは Windows のみ。

## トランザクション意味論

- **クラッシュリカバリを保証する**: プロセスが強制終了しても、次回起動時に未コミットのトランザクションを検出し、ロールバックまたはロールフォワードで復旧できる
- **完全ロールバック**: Abort 時、Txfio 自身がステージングした変更（`.txnew`、`CreateDirectory` が作ったディレクトリ、ジャーナルエントリ）を破棄する。`CreateDirectory` の中へ素のファイル API で書いたものも、そのディレクトリごと消す。それ以外の、このトランザクションが触れていないファイルは、開始前の状態に戻すのではなく、外部変更後の現在の状態のまま残る
- **複数ファイルにまたがる完全な原子性は保証しない**: コミット処理自体が複数ファイルへの一連のrename/削除操作であるため、コミット実行中は「一部が新状態、残りが旧状態」という中間状態が他プロセスから見えうる。これは最初の前提（ダーティリード許容）の範囲内として割り切る
- OSレベルのトランザクション機構（Transactional NTFS / TxF）は採用しない。Microsoftが非推奨としており将来のWindowsで削除される可能性があると明言している上、TxFは本質的に分離性(isolation)を提供する仕組みであり「ダーティリード許容」という方針とも噛み合わない

## 書き込みモデル（核となる設計判断）

**コミット時反映**。トランザクション実行中は、原則として本物のパスには触れない。変更内容は対象ファイルと同じディレクトリ内の `.txnew` サイドカーにステージングされる。例外は `CreateDirectoryAsync` で、空ディレクトリだけは呼び出した時点で本物のパスに作る。中身は素のファイル API で書く。同じプロセスでも別プロセスでもよい。配下では、親が最初からあるディレクトリと同じ Txfio の操作もできる。破棄ではそのディレクトリを中身ごと消す。

- Update: 新内容をまず `.txnew` に書き込み→fsync。コミット時に初めて `File.Move(txnew, target, overwrite: true)`（.NET Core 3.0以降のオーバーロード、Win32の`MOVEFILE_REPLACE_EXISTING`相当のアトミックな置換）で元のパスへ rename する。「削除してからMove」のような、対象パスが一瞬存在しなくなる方式は採らない
- Delete: コミットするまで対象（ファイルまたはディレクトリ）は無傷。ジャーナルに削除予約を記録するだけで、コミット時に初めて実際に削除する。ディレクトリは非再帰の `Directory.Delete`（親が空になってから消す）
- Add: `.txnew` として新規作成し、コミット時に本物のパスへrename
- Move/Rename: ジャーナルに「旧パス→新パス」を記録するだけで、コミット時に初めてrename

**この方式を選んだ理由**: 当初は「Update前に元ファイルをrenameで退避（`.txbak`）→新内容を元パスに直接書き込む」という即時反映方式を検討したが、これだと(1)複数回操作時にバックアップの世代管理が必要になる、(2)ロールバック時に退避したファイルを戻す往復操作が必要になる、という複雑さを抱える。コミット時反映に倒すことで、ロールバックは単に `.txnew` とジャーナルエントリを破棄するだけになり、`.txbak` による退避・リストア機構がまるごと不要になった。IOコスト自体は変わらない（データの実体を書くタイミングは同じ、変わるのは「本物のパスへの反映」をいつ行うかだけ）が、メンタルモデルが大幅に単純化される。

**トレードオフ**

- コミット処理が「変更ファイル数分のrename/削除をまとめて実行する」処理になり、コミット自体の所要時間は変更ファイル数に比例して伸びる。件数の進捗は未着手で、コピーの進捗は Add / Update の `TransferProgress` である
- アプリ自身が素のファイルIO（`File.ReadAllText`等）で自分がまだコミットしていない変更を読み返すことはできない。読み返す必要がある場合は専用 API `tx.ReadAsync(path)` を使う（「コミット後の姿」）。あるかどうかは `tx.ExistsAsync(path)`

**Updateの原子性**: `.txnew`への書き込みが完了する前にクラッシュしても、元のパスには常に「変更前の完全な状態」しか存在しない（コミット時反映により、コミット前は本物のパスに一切触れないため、この問題は書き込みモデルの選択によって自動的に解消される）。

**`.txnew`の命名規則**: ファイル名には対象ファイル名だけでなくトランザクションIDを含める（例: `foo.txt.{txid}.txnew`）。異常終了後に残った`.txnew`がどのトランザクションに由来するか、Recover 時に一意に特定できるようにするため。再ステージの退避は `foo.txt.{txid}.txnew.prev` である。ロールバックはこの退避も消す（「作成ディレクトリ」）。

**作成ディレクトリ**: `CopyAsync`、ディレクトリの `ImportAsync`、`ExtractArchiveAsync`、`ImportArchiveAsync` が作るディレクトリは、操作種別にはしない。`GetPendingChanges` には出さない。ジャーナル文書の `createdDirectories` に、絶対パスの配列で書く。欄が無い、または null のジャーナルは空として読む。文書の版は 1 のままである。`ExportAsync` がワークフォルダの外に作ったディレクトリは、この一覧に入れず、`RecoverAsync` でも消さない。ディレクトリのコピー、Import、展開は、作るディレクトリと各ファイルの Add をメモリに揃えてからジャーナルを 1 回書く。そのあとでディレクトリと `.txnew` を作る。ファイルが無く空ディレクトリだけのときも、一覧を書いてからディレクトリを作る。`AddAsync` と `UpdateAsync`、およびファイルの `CopyAsync` は、操作をジャーナルに書いてから `.txnew` を書く。`.prev` は、処理の途中で失敗したとき元の `.txnew` を戻すためだけに使う。作成や `.txnew` の書き込みに失敗したときは、その呼び出しで足した操作と作成ディレクトリを外し、作った実体を消してから、ジャーナルをもう一度書く。その書き込みにも失敗したときは、計画が残っているので次の `RecoverAsync` が消す。ロールバック（`Committing` が無い）は、操作に書いてある `.txnew` を消し、ワークフォルダ配下（`.txfio` を除く）の `.{guid}.txnew.prev` を消し、そのあと `createdDirectories` を深い順に、再帰せず消す。ディレクトリの削除に失敗したときは、その例外を再送出する。ジャーナルは残る。`Committing` があるときは作成ディレクトリを消さない。

**同一ディレクトリに置く理由とトレードオフ**: `.txnew`を対象ファイルと同じディレクトリに置くのは、コミット時のrenameを確実に同一ボリューム内（かつSMB上でメタデータのみの最速処理）で完結させるため。一方で、トランザクション実行中はエクスプローラー等の外部ツールから中間ファイルが見えてしまう（ダーティリード許容の前提内ではあるが、誤って削除・編集されるリスクはゼロではない）。

## API設計

**変更検出方式**: 操作ログ方式。ライブラリのAPI（Add/Update/Delete/DeleteTree/Move/Copy/CreateDirectory）を明示的に呼んだ操作のみを追跡する。ワークフォルダ全体をスキャンして差分を自動検出する方式（スナップショット比較）は、ネットワークファイルシステム上での全件スキャンコストが非機能要件と衝突するため採用しない。ディレクトリのコピーと Import、ディレクトリからの ZIP の作成と Export だけは、対象の木を読むために走査する。

**主なAPI**

- `AddAsync(path, Stream content, IProgress<TransferProgress>? progress, CancellationToken)` / `UpdateAsync(path, Stream content, IProgress<TransferProgress>? progress, CancellationToken)`: 別APIとして明示的に分ける。存在有無の事前確認IOを省け、呼び出し側の意図とファイルシステムの実態が食い違っている場合を早期に検出できる。`progress` は省略でき、null のときは通知しない。操作をジャーナルに書いてから `.txnew` を書く（「作成ディレクトリ」）
- `DeleteAsync(path, CancellationToken)`: ファイルとディレクトリの両方。公開する操作種別はどちらも `Delete`（ディレクトリ用の別 Kind は足さない）。ディレクトリだったことはジャーナル内部に残し、コミット検証ですり替わっていれば失敗とする。配下すべての削除は `DeleteTreeAsync`
- `DeleteTreeAsync(path, CancellationToken)`: ディレクトリとその配下すべてを、種別 `DeleteTree` の 1 件として予約する。ファイルなら `UnsupportedOperationException`。空ディレクトリは予約できる。ステージでは木を走査しない。コミット時に `Directory.Delete(path, recursive: true)`。このトランザクションの操作が配下にあるときは `InvalidOperationException`。予定されているディレクトリ Move の移動元または移動先そのものなら、Move を消して元ディレクトリの `DeleteTree` に置き換える。配下への全削除は拒否する。実行中はワークフォルダの哨兵を排他で持つ。落ちたあとは、ディレクトリが残っていれば再実行し、消えていれば適用済み、ファイルにすり替わっていれば `ConflictDetected`。中の残骸も消すので、呼び側は先に `RecoverAsync` する
- `MoveAsync(oldPath, newPath, CancellationToken)`: 同一ボリューム内のファイルとディレクトリの移動。ボリューム跨ぎはエラーにする（コピー+削除への暗黙のフォールバックはしない）。正規化した絶対パスが大文字小文字を無視して同じとき（表記だけの違い、または完全に同じ文字列）は `InvalidOperationException`。ファイルもディレクトリもジャーナルに載せず、ロックも取らない。ディレクトリはコミット時に 1 回だけ rename し、中身はそれに付いていく。子はジャーナルに書かず、木は走査しない。検証は存在だけ。移動先が空いているか、既にこのトランザクションの別の Move の移動元であるときだけ受け付ける（「Move の連鎖」）。自分自身の配下、および移動元・移動先の配下への操作は `InvalidOperationException`。ディレクトリ自身の連続 Move と、そのあとの Delete はファイル Move と同じ畳み込みで、Delete は直下の規則のまま。実行中はワークフォルダの哨兵を排他で持つ
- `CreateDirectoryAsync(path, CancellationToken)`: 空ディレクトリを、呼び出した時点で本物のパスに作る。中身は素のファイル API で書く。同じプロセスでも別プロセスでもよい。ジャーナルは種別 `CreateDirectory` の 1 件で、配下は走査しない。素のファイル API で書いたファイルは `GetPendingChanges` に出ない。進捗は無い。呼び出した時点でパスが存在する、親が無い、ワークフォルダ自身、`.txfio` 配下は失敗する。親は作らない。ジャーナルへ書いてからディレクトリを作る。作成に失敗したらそのエントリを消す。記録のあと作成の前に落ちたとき、ディレクトリが無ければロールバックの削除は何もしない。配下では、親が最初からあるディレクトリと同じ操作ができる（Add / Update / Delete / DeleteTree / Move / Copy / Import / 入れ子の `CreateDirectory` / ファイルの `ReadAsync` / `ExportAsync` / 文字列と JSON）。各操作はその操作の既存の規則に従う。`GetPendingChanges` には、この 1 件に加えて配下の操作も出る。そのパス自身への Delete / DeleteTree / Move の元と先 / Update / もう一度の `CreateDirectory` は `InvalidOperationException` で、畳まない。そのパスをコピー元にすることは、配下にこのトランザクションの操作が無いときに限って許す。操作があるときは `CopyAsync` と同じく `InvalidOperationException` になる。`CreateDirectory` 自身のエントリは配下の操作に数えない。操作が無いときの走査には、未コミットの Add は含まれない。`ExportAsync` は許す。移動元にはできない。コピー先や Import 先がそのパス自身のときは、先が既にあるので失敗する。木の外への操作と、兄弟の `CreateDirectory` は同じトランザクションで続けられる。コミットではディレクトリの中身を見ず、ディレクトリが残っていれば成功としてその場所に残す。rename しない。配下の操作はいつもの順で適用する。無い、またはファイルに変わっていれば適用前に `Failed`。適用順は Add / Move と同じ群で、適用は存在の確認だけ。適用開始後にディレクトリが無い、またはファイルなら、その操作は `PartialConflict` として続行する。破棄と、`Committing` が無いジャーナルの `RecoverAsync` は `Directory.Delete(path, recursive: true)`。中の `.txnew`、素のファイル API で書いたもの、コピーが作ったサブディレクトリも消える。開始前からあったファイルを素の API で中へ動かしても、破棄で消える。入れ子の `CreateDirectory` は親を消すと一緒に消える。既に無ければ何もしない。削除に失敗した例外は呼び出し側へ届き、ジャーナルが残っていれば次の `RecoverAsync` が同じ削除をする。`Committing` のあとで落ちたときは、ディレクトリがあれば残したまま進め、配下の操作もいつもの復旧に従う。無ければ、またはファイルなら `ConflictDetected`。ロックはそのディレクトリと、ワークフォルダの排他哨兵。哨兵はトランザクションが終わるまで持つ。既に共有なら排他へ上げる。配下の操作は、それぞれのパスロックも取る。祖先の `DeleteTree` やディレクトリ Move は、配下にこの操作があるため失敗する
- `CopyAsync(source, dest, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダ内のファイルまたはディレクトリをコピーする。コピー元は残す。同じボリュームでもバイトをコピーする。ファイルは Add をジャーナルに書いてから `.txnew` を書き、Add として残す。ディレクトリはディスク上の姿を歩き、各ファイルをコピー先の `.txnew` にし、空のサブディレクトリも作る。ジャーナルに残るのは各ファイルの Add で、空ディレクトリの種別は足さない。作るディレクトリと Add は実体より先にジャーナルへ書く（「作成ディレクトリ」）。このトランザクションの `.txnew` は除外する。未コミットの Add は本物の名前でディスクに無いので含まれない。ジャンクションとシンボリックリンクは辿らず、そのエントリもコピーしない。ディレクトリコピーのあいだは哨兵を排他で持つ。ファイルコピーは共有哨兵に加え、コピー元とコピー先をロックする。コピー先にファイルかディレクトリがあれば失敗し、混ぜない。コピー先自身とその空のサブディレクトリはこの操作が作り、親が無いときは失敗する。同一パス、コピー先がコピー元の配下、コピー元かコピー先の配下にこのトランザクションの操作があるとき、そのパス自身が既に操作済みのとき、予定された `DeleteTree` の配下へのコピーは `InvalidOperationException`。失敗か取り消しでは、作りかけの `.txnew` と、この操作が作ったディレクトリを消す。`progress` は省略でき、null のときは通知しない
- `ImportAsync(externalPath, targetPath, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダの外にあるファイルまたはディレクトリを、`CopyAsync` と同じ規則で `.txnew` へコピーし、Add として残す。コピー元は消さない。同じボリュームでもコピーする。コピー元がワークフォルダの中なら `ArgumentException`。ディレクトリのあいだは哨兵を排他で持つ。ファイルはロックがコピー先だけで、哨兵は共有。失敗か取り消しでは書きかけの `.txnew` と、この操作が作ったディレクトリを消す。ディレクトリを作る順は `CopyAsync` と同じ（「作成ディレクトリ」）。`progress` は省略でき、null のときは通知しない
- `ExportAsync(path, externalPath, IProgress<TransferProgress>? progress, CancellationToken)`: ファイルは `ReadAsync` と同じバイトを、ディレクトリは配下の各ファイルを `ReadAsync` と同じバイトで、ワークフォルダの外へコピーする。ジャーナルには残さず、ワークフォルダのファイルは変えず、ロックもしない。ディレクトリでもロックしない。コピー先がワークフォルダの中なら `ArgumentException`。外にファイルかディレクトリがある、または親が無いときは `ExternalConflictException`。上書きも、コピー先の親の自動作成もしない。コピー先のディレクトリ自身とその空のサブディレクトリは、この操作が作る。ジャンクションとシンボリックリンクは辿らず、そのエントリもコピーしない。作りかけは失敗か取り消しで消し、成功したファイルとディレクトリは Dispose しても残る。`progress` は省略でき、null のときは通知しない
- `ReadAsync(path, CancellationToken)`: 「コミット後の姿」のファイルを、位置 0 の読み取りストリームで返す。破棄は呼び出し側。全体はメモリにコピーしない。ロックは取らず、ジャーナルにも書かない。姿でファイルが無いときは `ExternalConflictException`。ディレクトリは未対応。開き方は `FileShare.Read | FileShare.Delete` なので、ストリームを閉じる前でもコミットの rename は進む。同じパスの再ステージは、ストリームを閉じるまで失敗しうる。取り消しは呼び出し開始時だけ有効
- `ExistsAsync(path, CancellationToken)`: 「コミット後の姿」でファイルかディレクトリがあるなら true、無ければ false。無いことは例外にしない。ディレクトリだからという理由では失敗しない。ロックは取らず、ジャーナルにも書かない。パスがワークフォルダの外なら `ArgumentException`。メタデータ配下、および呼び出しが重なっているときは `InvalidOperationException`。取り消しは呼び出し開始時だけ有効
- `tx.GetPendingChanges()`: 現在のジャーナル内容（Add/Update/Delete/DeleteTree/Move/CreateDirectory 一覧）を返す。ディレクトリのコピーは各ファイルの Add として見える。作成ディレクトリの一覧は出さない。`CreateDirectory` の配下で予約した操作は、その操作として見える。素のファイル API で書いたファイルは出ない。実装コストはほぼゼロ（ジャーナルをそのまま返すだけ）で、git status に相当するデバッグや UI 表示に使う

**ZIP アーカイブ**

`System.IO.Compression.ZipArchive` を包んだ `ITransaction` のメソッドである。拡張メソッドにはしない。失敗時の後始末、哨兵、ロックに内部の仕組みが要るからである。ZIP 全体をメモリや一時ファイルに作らず、`.txnew` や外部の ZIP へ直接書く。扱うのは ZIP だけで、tar、GZip 単体、Brotli は対象外である。内と外の区別は `CopyAsync` / `ImportAsync` / `ExportAsync` と同じで、反対側のパスを渡すと `ArgumentException` になる。`compressionLevel` は省略時 `Optimal`、`includeBaseDirectory` は省略時 false（含めない）、`entryNameEncoding` と `progress` は省略時 null である。

- `CreateArchiveAsync(source, archivePath, CompressionLevel compressionLevel, bool includeBaseDirectory, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダ内のファイルまたはディレクトリを、ワークフォルダ内の ZIP にする。ZIP は `.txnew` へ書いて Add の 1 件として残す。入力の読み方は `CopyAsync` と同じで、ディスク上の姿を歩く。このトランザクションの `.txnew` は除外し、未コミットの Add は含まれない。ジャンクションとシンボリックリンクは辿らず、ZIP にも入れない。空のサブディレクトリはディレクトリエントリとして入れる。ファイルのときはファイル名のエントリ 1 つで、`includeBaseDirectory` は見ない。エントリ名の区切りは `/` で、エンコーディングは .NET の既定（ASCII 以外を含む名前は UTF-8）に固定する。エントリの日時は読んだファイルの最終更新日時である。ZIP のパスにファイルかディレクトリがある、または親が無いときは `ExternalConflictException`。上書きしない。ZIP のパスが入力ディレクトリの配下、入力の配下か ZIP のパスにこのトランザクションの操作がある、そのパス自身が既に操作済み、予定された `DeleteTree` の配下への作成は `InvalidOperationException`。ロックは `CopyAsync` と同じで、ディレクトリなら排他の哨兵のあとに入力と ZIP のパス、ファイルなら共有の哨兵に加えて入力と ZIP のパスをロックする。失敗か取り消しでは、書きかけの `.txnew` を消す
- `ExportArchiveAsync(source, externalArchivePath, CompressionLevel compressionLevel, bool includeBaseDirectory, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダ内のファイルまたはディレクトリを、ワークフォルダの外の ZIP にする。入力の読み方は `ExportAsync` と同じで、ファイルは `ReadAsync` と同じバイトを、ディレクトリは配下の各ファイルを `ReadAsync` と同じバイトで読む。ジャーナルには残さず、ワークフォルダのファイルは変えず、ロックもしない。エントリの作り方と引数は `CreateArchiveAsync` と同じである。ZIP のパスがワークフォルダの中なら `ArgumentException`。外にファイルかディレクトリがある、または親が無いときは `ExternalConflictException`。上書きも、親の自動作成もしない。作りかけの ZIP は失敗か取り消しで消し、成功した ZIP は Dispose しても残る
- `ExtractArchiveAsync(archivePath, destinationDir, Encoding? entryNameEncoding, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダ内の ZIP を、ワークフォルダ内の新しいディレクトリへ展開する。ZIP は `ReadAsync` と同じバイトで読むので、同じトランザクションで作った未コミットの ZIP も展開できる。各ファイルは `.txnew` へ書いてファイルごとの Add として残す。展開先のディレクトリ自身と、エントリにあるディレクトリは `CopyAsync` と同じくこの操作が作り、空ディレクトリの種別は足さない。作るディレクトリと Add は実体より先にジャーナルへ書く（「作成ディレクトリ」）。展開したファイルの最終更新日時はエントリの日時にし、それを設定した `.txnew` から Add の After を取る。`entryNameEncoding` は UTF-8 フラグの無いエントリ名（Shift_JIS など）の読み方で、省略時は .NET の既定。展開先にファイルかディレクトリがある、または親が無いときは `ExternalConflictException`。混ぜず、上書きしない。展開先の配下にこのトランザクションの操作がある、そのパス自身が既に操作済み、予定された `DeleteTree` の配下への展開は `InvalidOperationException`。排他の哨兵のあとに展開先をロックする。失敗か取り消しでは、書きかけの `.txnew`、この操作で足した Add、この操作が作ったディレクトリを消す
- `ImportArchiveAsync(externalArchivePath, destinationDir, Encoding? entryNameEncoding, IProgress<TransferProgress>? progress, CancellationToken)`: ワークフォルダの外の ZIP を、`ExtractArchiveAsync` と同じ規則でワークフォルダ内の新しいディレクトリへ展開する。ZIP は消さない。ZIP のパスがワークフォルダの中なら `ArgumentException`。外の ZIP が無いときは `ExternalConflictException`
- 展開（`ExtractArchiveAsync` / `ImportArchiveAsync`）は、書き始める前にセントラルディレクトリのエントリ名を全部検証する。1 つでも次に当たれば何もステージせず、展開先も作らずに `InvalidDataException` で失敗する。展開先の外へ出る名前（`..`、絶対パス、ドライブ指定）、Windows のパスに使えない名前、`.txnew` で終わる名前、名前の重複（`ToUpperInvariant` で畳んで比べるので、大文字と小文字の違いだけのものも重複）、同じ名前がファイルとディレクトリの両方で出るもの。ZIP 自体が壊れている、暗号化されているなど .NET が読めないときは、`System.IO.Compression` の例外のまま外に出す
- 入れるファイルと名前を組で指定する作成: `CreateArchiveAsync(IEnumerable<ArchiveEntrySource> entries, archivePath, CompressionLevel compressionLevel, IProgress<TransferProgress>? progress, CancellationToken)` と、同じ形の `ExportArchiveAsync(entries, externalArchivePath, …)`。1 つのパスを渡す版は残す。`ArchiveEntrySource(string SourcePath, string? EntryName = null)` は公開の record で、`SourcePath` はワークフォルダ内のファイルまたはディレクトリ、`EntryName` は ZIP の中の名前である。`includeBaseDirectory` は持たない。`entries`、要素、`SourcePath` が null なら `ArgumentNullException`。`entries` は最初に 1 回だけ列挙する
  - `EntryName` の省略時は、ワークフォルダからの相対パス（区切りは `/`）。`\` は `/` に直す。ディレクトリの要素だけ、`""` なら中身を ZIP のルートに置く。ファイルの要素で `""` は `ArgumentException`
  - ディレクトリの要素は、1 つのパスを渡す版と同じく配下を再帰で入れ、エントリ名はその名前の下になる。空のサブディレクトリはディレクトリエントリにする
  - エントリ名は展開と同じ規則（展開先の外へ出る名前、Windows のパスに使えない名前、`.txnew` で終わる名前、大文字と小文字を無視した重複、ファイルとディレクトリの同名）で検証し、当たれば `ArgumentException`。渡された名前はロックと IO の前に、ディレクトリを歩いてできる名前はロックのあと書き始める前に調べる。後者で失敗したときも ZIP は残さない
  - 同じ `SourcePath` を別のエントリ名で 2 回入れるのは許す。空のリストはエントリの無い ZIP になる
  - 読み方は 1 つのパスを渡す版と同じである。Create はディスク上の姿を読み、要素がこのトランザクションでステージ済み、ディレクトリの要素の配下にこのトランザクションの操作がある、予定された `DeleteTree` の配下のときは `InvalidOperationException`。Export は `ReadAsync` と同じバイトを読み、ディレクトリは `ExportAsync` と同じく歩く。ZIP のパスが要素と同じ、またはディレクトリの要素の配下なら `InvalidOperationException`
  - Create のロックは、要素にディレクトリが 1 つでもあれば排他の哨兵、ファイルだけなら共有の哨兵で、どちらも各要素と ZIP のパスをロックする。Export はロックしない
  - ZIP の中はリストの順で、ディレクトリの中身はその位置で列挙した順である。進捗は圧縮前のバイト数の合計で、`TotalBytes` は null
- `GetPendingChanges` には、作成は ZIP の Add の 1 件、展開は各ファイルの Add として見える。`ExportArchiveAsync` は出ない

**コミット後の姿**: `ReadAsync`、`ExistsAsync`、文字列と JSON の読み書きは、このトランザクションの予約をコミットしたあとの姿で扱う。ワークフォルダ全体は走査しない。問い合わせたパスと操作一覧だけを見る。`Add` / `Update` はその `.txnew`。ファイル `Move` の移動先は移動元のバイトで、移動元は無い。移動元へ `Add` したときはその `.txnew`。同じパスが `Move` の移動元でも移動先でもあるときは、連鎖を空いている端から適用したあとのバイトを読む。`Delete` の対象は無い。`DeleteTree` の対象とその配下は無い。ディレクトリ `Move` の移動元とその配下は無い。移動先の配下は、移動元の対応するパス。同じディレクトリが連鎖で移動元でも移動先でもあるときは、空いている端から適用したあとの木。`CreateDirectory` の配下は、各操作の既存の規則と、素のファイル API で書いたファイルのまま。どれにも当たらなければディスク上のファイル。ディレクトリそのものの `ReadAsync` は未対応のまま。`ExistsAsync` はディレクトリでも bool を返す

**文字列と JSON（拡張メソッド）**

`ITransaction` には足さない。名前空間 `Txfio` の拡張メソッドで、同期版は無い。`IProgress` も付けない。中身はいったんメモリに載せて、既存の `ReadAsync` / `AddAsync` / `UpdateAsync` を呼ぶ。大きいバイト列は `Stream` の API を使う。

- `ReadAllTextAsync` / `ReadAllLinesAsync`: `ReadAsync` と同じバイトを文字列、または行の配列にする。行の区切りと、戻り値に改行を含めないことは `File.ReadAllLinesAsync` に合わせる。エンコーディングを省略した読みは `File` と同じ BOM 検出。エンコーディング引数のあるオーバーロードも持つ。ディレクトリ、および対象が無いときは `ReadAsync` と同じ例外
- `WriteAllTextAsync` / `WriteAllLinesAsync`: 「コミット後の姿」でファイルが無ければ `Add`、あれば `Update`。`Move` の移動先は `Update`。ファイルなら移動先の Add と元の Delete に畳む。ファイル `Move` の移動元は `Add`。ディレクトリ `Move` の移動先も移動元も `InvalidOperationException`。同じトランザクションで続けて書くと、既存の再ステージに乗る（新規のままなら `Add`、既存なら `Update`。`Delete` のあとは、姿では無いので選ぶ時点は `Add` だが、畳み込みで記録は `Update`）。エンコーディングを省略した書きは BOM なし UTF-8。`WriteAllLinesAsync` の改行は `File.WriteAllLinesAsync` に合わせる。`WriteAllTextAsync` の内容が null なら空として書く。`WriteAllLinesAsync` の内容が null、またはエンコーディング引数が null なら `ArgumentNullException`
- `ReadFromJsonAsync<T>`: `ReadAsync` のバイトを `System.Text.Json` でデシリアライズする。失敗は `System.Text.Json` の例外のまま。`JsonSerializerOptions` は省略でき、省略時は既定
- `WriteAsJsonAsync<T>`: シリアライズした JSON を、`WriteAllTextAsync` と同じ Add / Update の規則で書く。`JsonSerializerOptions` は省略でき、省略時は既定

**共通方針**

- 全ての書き込み系APIは非同期（`Task`ベース）で統一する。文字列と JSON の拡張も同じで、同期版は置かない。IOが遅い環境を主眼に置く以上、非同期ファーストが自然で、同期版の二重メンテコストの方が問題になる
- `CancellationToken` を受け付けるが、有効なのはコミット開始前（検証フェーズまで）に限る。物理的な適用（rename/削除の実行）が始まったら`CancellationToken`は無視し、最後まで完了させる。これにより「意図的な中断」と「クラッシュによる中断」を明確に区別できる（前者はコミット中には起こり得ず、後者だけが`RecoverAsync()`の対象になる）。コミット開始前にキャンセルされた場合は`DisposeAsync()`内で通常の非同期ロールバック（`.txnew`削除、コピーが作ったディレクトリの削除、`CreateDirectory` の再帰削除、ロック解放、ジャーナル削除）を行いクリーンに終了する
- `AddAsync` と `UpdateAsync`、`CopyAsync`、Import の `.txnew` へのコピー、Export の外部へのコピー、および ZIP の 4 メソッドは、`IProgress<TransferProgress>?` を `CancellationToken` の直前に受け取る。null のときは通知しない。`TransferProgress` は書き終えたバイト数と、開始時点の残りバイト数（シークでき長さを読めるとき。残りが 0 未満なら null）である。81920 バイトを書き終えるたびに通知し、空の内容は最後に 1 回だけ通知する。ストリームは巻き戻さない。ディレクトリのコピーでは全体サイズを事前に測らず、`TotalBytes` は null、書き終えたバイトの合計を通知する。空のディレクトリは最後に 1 回、0 バイトを通知する。Add / Update が通知するのは呼び出し側の内容を `.txnew` へ書くコピーだけで、再ステージの退避とジャーナル書き込みは含めない。ZIP の作成と Export は、読み込んだ元ファイルのバイト数（圧縮前）の合計を通知し、`TotalBytes` は null である。展開と Import は、`.txnew` へ書いた展開後のバイト数の合計を通知し、`TotalBytes` はセントラルディレクトリの `Length` の合計である。どれも空なら最後に 1 回、0 バイトを通知する。`Delete` / `DeleteTree` / `Move` / `CreateDirectory` / `Commit` と、コミット件数の進捗は対象外である
- エラーハンドリングは例外ベース。基底は `TxfioException`。ディスク上の前提が崩れたときは `ExternalConflictException`（失敗したパスを 1 つ持つ。対象が無い、既にある、移動先がディレクトリ、親やワークフォルダが無い、ディレクトリ直下に予定外の子がある、コピー先が塞がっている）。未対応（ファイルへの `DeleteTreeAsync`、ボリュームをまたぐ Move、ディレクトリの `ReadAsync`）は `UnsupportedOperationException`。使い方の誤り（同じトランザクションへの重なった呼び出しを含む）は `InvalidOperationException` と `ArgumentException` のまま。ZIP の展開で危険なエントリ名があるときは `InvalidDataException`、ZIP 自体が読めないときは `System.IO.Compression` の例外のまま。他のトランザクションがパスまたはワークフォルダを押さえているときは `LockContentionException`（失敗したパスを 1 つ持つ。内部例外は持たない）。持ち主のいない残骸ジャーナルが残っているあいだの `BeginAsync` と `CommitAsync` は `RecoveryRequiredException`（`Path` はワークフォルダ。内部例外は持たない）。読めるジャーナルは `RecoverAsync` で解消する。JSON として読めないジャーナルは消さないので、直すか消すまで `RecoveryRequiredException` のままである。コミットの成否は例外にせず `CommitReport` で返す。全体の結果は `CommitResult`（「結果の詳細」）

**トランザクションのライフサイクル**

- `await using var tx = await Txfio.BeginAsync(path);` のパターンで表現し、`IAsyncDisposable`とする。`Committing` を書く前に Commit されず Dispose された場合は、自動的にロールバックする。`Committing` を書いたあとに適用で例外が出たら、その例外を再送出する。Dispose はロールバックせず、ロックだけ閉じる。ジャーナルと `.txnew` は残り、次の `RecoverAsync` がロールフォワードする

**ディレクトリ操作**

- 直下だけのディレクトリ削除は `DeleteAsync` で予約する。空ディレクトリは削除してよい。コミットまで実体は残す。配下すべての削除は `DeleteTreeAsync`
- 見るのは直下だけ。ネストした木は子ディレクトリを先に `DeleteAsync` してから親を消す（`DeleteAsync` の 1 回で子孫を再帰削除しない）。直下に、このトランザクションの削除予約・Add の打ち消し・ディレクトリ外への Move のいずれでもない子（ファイルまたはディレクトリ）がある場合はエラーとする。Update / 残る Add / `CreateDirectory` がある親もエラー。削除予約済みディレクトリへの Add / Update / Move / `CreateDirectory` 入り、そのディレクトリ自身を Move 先にすることもエラー。「何が変更対象か」を常に明示的にする操作ログ方式の思想と整合させ、暗黙の巻き込みを避ける
- 配下すべての削除は `DeleteTreeAsync` で明示する。ステージでは木を走査せず、ジャーナルは `DeleteTree` の 1 件、コミットで再帰削除する。実行中は哨兵を排他で持つ。このトランザクションの操作が配下にあるときは拒否する
- 直下スキャンではこのトランザクションの `.txnew` を除外する。別トランザクションの残骸サイドカーや、それ以外のエントリは未追跡としてエラー
- ワークフォルダ自身は削除対象外。メタデータフォルダ（`.txfio`）とその配下は、すべての書き込み系 API で操作対象外（エラー）
- 親ディレクトリが存在しないパスを指定した場合、自動作成はせずエラーにする。例外は `CopyAsync` とディレクトリの `ImportAsync` / `ExportAsync` が、コピー先のディレクトリ自身とその空のサブディレクトリを、その操作の一部として作ること。`ExtractArchiveAsync` / `ImportArchiveAsync` も、展開先のディレクトリ自身とエントリにあるディレクトリを同じく作る。コピー先の親が無いときはエラーのまま。`CreateDirectoryAsync` は対象の空ディレクトリだけを作り、親は作らない
- `CreateDirectoryAsync` は、空ディレクトリを本物のパスにすぐ作る。中身は素のファイル API で書く。同じプロセスでも別プロセスでもよい。配下では、親が最初からあるディレクトリと同じ操作ができる。ジャーナルの `CreateDirectory` は 1 件で、配下は走査しない。配下の操作はそれぞれのジャーナルエントリになる。破棄と、適用開始前に落ちたあとの `RecoverAsync` は、そのディレクトリを中身ごと消す。コミットではディレクトリを残し、配下の操作を適用する。詳細は主な API の `CreateDirectoryAsync`
- ディレクトリの Move は `MoveAsync` で予約する。コミット時にディレクトリを 1 回だけ rename し、中身はそれに付いていく。子はジャーナルに書かず、木は走査しない。検証は存在だけ。自分自身の配下へは移せない。移動元と移動先の配下への操作は拒否する。ディレクトリ自身の連続 Move と、そのあとの Delete はファイル Move と同じ畳み込みで、Delete は直下の規則のまま。実行中はそのトランザクションがワークフォルダの哨兵を排他で持ち、他の変更操作を止める。落ちたトランザクションの `.txnew` が中にあっても走査しないので、呼び側は先に `RecoverAsync` する

## 並行性とロック

**サポート範囲**: 単一プロセス内での複数トランザクション並行実行、および複数プロセス（別exe/別マシン）からの同時アクセスの両方をサポートする。ただしこのロックはTxfio利用者間の協調ロックであり、通常のFile API（Txfioを経由しない直接操作）による変更までは防げない

**同じインスタンス**: 1 つの `ITransaction` の公開メンバーは、重なって呼べない。先に入った呼び出しは最後まで行い、後から重なった呼び出しは状態を変える前に `InvalidOperationException` にする。`CommitAsync`、`DisposeAsync`、`GetPendingChanges`、`ReadAsync`、`ExistsAsync`、変更系を含む。進行中の `IProgress` から同じインスタンスを呼ぶのも同じ例外である。呼び出しが終わったあとは、また 1 つずつ呼べる。重なった側は記録しない。`ExtractArchiveAsync` がその操作の中で読むのは、公開メンバーの重なりではない。別のトランザクション同士の並行は、この節のロックのままである

**ロック粒度**: 操作が名指ししたパスだけをロックする。ファイルでもディレクトリでも、そのパスの `.txfio/locks/{16進}.lock` を作る。ハッシュする文字列はワークフォルダからの相対パスで、区切りは `\`、`ToUpperInvariant` で畳んだ UTF-8 の SHA-256 である。加えて、`Add` / `Update` / `Delete` / `DeleteTree` / `Move` / `Import` / `Copy` / `CreateDirectory` / `CreateArchive` / `ExtractArchive` / `ImportArchive` はパスロックの前にワークフォルダの哨兵（相対パス `.`）を共有で取り、トランザクションが終わるまで持つ。`Read` と `Exists` と `Export` と `ExportArchive` はディレクトリでも取らない。ディレクトリの Delete はそのディレクトリだけで、子はロックしない。異なるパスを触るトランザクション同士は、哨兵を共有しているあいだ並行実行できる。ディレクトリ Move、`DeleteTree`、ディレクトリの `CopyAsync`、ディレクトリの `ImportAsync`、`CreateDirectory`、ディレクトリからの（組ならディレクトリを含む）`CreateArchiveAsync`、`ExtractArchiveAsync`、`ImportArchiveAsync` は哨兵を排他で取る。`CreateDirectory` はそのディレクトリもロックする。配下の操作は、それぞれの通常のパスロックも取る。ファイルの `CopyAsync` は共有哨兵に加え、コピー元とコピー先をロックする。ディレクトリの `CopyAsync` は、排他の哨兵のあとにコピー元とコピー先をロックし、子はロックしない。ディレクトリの Import は、排他の哨兵のあとにコピー先をロックする。`CreateArchiveAsync` は `CopyAsync` と同じく入力と ZIP のパスをロックする。組で指定する `CreateArchiveAsync` は、要素にディレクトリがあれば排他、ファイルだけなら共有の哨兵のあとに、各要素と ZIP のパスをロックする。`ExtractArchiveAsync` と `ImportArchiveAsync` は、排他の哨兵のあとに展開先をロックし、展開元の ZIP と子はロックしない

**競合検知のタイミング**: Pessimistic。使い方の誤り（パスの解決、メタデータ配下、未対応、二重ステージ）を先に返し、そのあとでロックを取る。取れなければ待たずに `LockContentionException` を返す。ロックのあとでディスク上の前提が崩れた場合や、`.txnew` とジャーナルの書き込みに失敗した場合は、そのトランザクションが終わるまで持ち続ける。同じトランザクションが同じパスを再び触るときは開き直さない。IO が遅いファイルサーバー環境では、せっかく進めた作業がコミット直前に無駄になる Optimistic 方式のリスクが大きいため、Pessimistic を採る。

**Move操作時のロック**: 旧パス・新パスの両方に対してロックを取得する（他のトランザクションが移動先パスに書き込もうとする競合を防ぐため）。取得順序は、哨兵が先で、そのあと大文字化した絶対パスの辞書順とする（`Move(A→B)`と`Move(B→A)`が同時に走ってもデッドロックしない）。2本目が取れなくても、1本目は持ち続ける。同じパスへの Move はロックせず、`InvalidOperationException` にする。ディレクトリ Move、`DeleteTree`、ディレクトリの `CopyAsync`、ディレクトリの `ImportAsync`、`CreateDirectory`、ディレクトリからの `CreateArchiveAsync`、`ExtractArchiveAsync`、`ImportArchiveAsync` は哨兵を排他で取る。既に共有を持っていればいったん閉じて排他を開く。排他を開けなかったときも共有に戻す。開けたあと、自分以外の `.lock` を `FileShare.None` で開き、使用中があればその操作は積まず共有に戻して `LockContentionException`（`Path` はワークフォルダ）にする。共有を開き直せないときは握りつぶさない。共有違反なら同じ例外のまま、哨兵を失ったことを覚えて次のロック取得で開き直す。共有違反以外の失敗はその例外のまま返す。確認したハンドルはすぐ閉じ、`.lock` は消さない。通ったあとに移動元と移動先をロックする

**ロック機構とデッドプロセスの検知**: `.lock`ファイルは、作成するだけでなく`FileShare.None`で開いたままハンドルを保持し続けるOSファイル共有ロックとして実装する。プロセスがクラッシュ・強制終了すると、OS/SMBサーバーが自動的にハンドルを解放する。テストがコミットを途中で止めたときも、ハンドルだけ閉じる。リース・ハートビート・stale判定・奪取の競合処理は不要である。他のトランザクションはロック取得を試みて共有違反（Sharing Violation）が起きれば「使用中」、開ければ「デッドプロセスの残骸」と判定できる。ただし、クライアント切断からSMBサーバー側がハンドルを解放するまでの遅延はSMBサーバー実装（Windows Server SMB共有、各種NAS製品等）に依存し標準化された保証があるわけではないため、実際のファイルサーバー環境での検証を前提とする

**ロックとジャーナルの紐付け**: ロックの解放はハンドルを閉じることである。プロセスが落ちると OS がハンドルを閉じ、テストがコミットを途中で止めたときの Dispose もハンドルだけ閉じる。`.lock` ファイルは残す。Recover はジャーナルを処理するあいだワークフォルダの哨兵を排他で持つ（「Recover API」）。それ以外のパスの `.lock` は開かず消さない（トランザクションの生存ロックは下記のとおり開く）。ハンドルが閉じたあと、Recover より先に別のトランザクションが同じパスの `.lock` を取り直してよい。ただし残骸ジャーナルが残っているあいだは、そのトランザクションは開始もコミットもできない（「残骸ジャーナルがあるあいだの拒否」）。落ちたトランザクションの Before / After は、ディレクトリでは存在しか見ないので、落ちたあとに配下へ確定したデータを見分けられないためである。`.lock` ファイルの掃除は将来のメンテナンスに残す

**トランザクションの生存ロック**: パスロックとは別に、トランザクションごとに `.txfio/tx-{guid}.lock` を 1 つ持つ。`{guid}` はジャーナル `.txfio/tx-{guid}.journal` と同じで、`.txfio/locks/` には置かない。`BeginAsync` はジャーナルを書く前に、`FileMode.CreateNew`・`FileShare.None`・`FileOptions.DeleteOnClose` で開く。開けなければジャーナルを書かずに例外を返す。ジャーナルを書けなかったときは生存ロックを閉じる。ハンドルはトランザクションが終わるまで持つ。コミットの完了と破棄では、ジャーナルを消したあとで閉じる。ジャーナルの削除に失敗しても閉じる。残ったジャーナルは次の `RecoverAsync` が処理する。テストがコミットを途中で止めたときの Dispose は、ジャーナルを残したままハンドルだけ閉じる。プロセスが落ちると OS がハンドルを閉じ、`DeleteOnClose` によってファイルも消える。消えずに残っても害は無い

**残骸ジャーナルがあるあいだの拒否**: 残骸ジャーナルとは、`.txfio/tx-{guid}.journal` があり、その生存ロックを Recover と同じ開き方（`FileMode.OpenOrCreate`・`FileShare.None`・`FileOptions.DeleteOnClose`）で開けて、開けたあともジャーナルが残っているものをいう。確認はワークフォルダ全体で行い、パスごとには比べない。`.txfio/*.journal` を一覧し、それぞれの生存ロックを開いてすぐ閉じる。ジャーナルは読まない。共有違反なら生きているとみなす。自分のジャーナルは自分が生存ロックを持つので必ず共有違反になる。共有違反以外の失敗はその例外のまま返す。確認するのは次の 2 か所である

- `BeginAsync`: 生存ロックを作る前。残骸があれば何も作らずに `RecoveryRequiredException` を返す
- `CommitAsync`: 操作が 1 件以上あるとき、前提条件の検証と Before / After の記録より前。残骸があれば実体に触れず、`Committing` を書かずに `RecoveryRequiredException` を返す。トランザクションは未コミットのまま残り、破棄すればロールバックする。このトランザクションは共有の哨兵を持つので、`RecoverAsync` は破棄してから呼ぶ

`BeginAsync` を済ませたあとで別のトランザクションが落ちることがあるので、開始時の確認だけでは足りず、コミット時にも確認する。コミット時の確認は共有の哨兵を持って走るので、排他の哨兵を持つ `RecoverAsync` とは重ならない。生存ロックを開けるのは一度に 1 つのハンドルだけなので、別のトランザクションの確認が同じ残骸の生存ロックをちょうど開いているあいだは、共有違反で生きているとみなしてしまう。このごく短い窓は許容し、再試行はしない（生きているトランザクションは必ず共有違反になり、待つとコミットのたびに遅れる）

## ジャーナルとリカバリ

**物理配置**: ワークフォルダ内に隠しメタデータフォルダ（例: `.txfio/`）を1つ作り、ジャーナルとロックファイルをそこに集約する。ジャーナル/ロックは頻繁に読み書きする小さい制御ファイルなので一箇所にまとめた方が管理しやすく、外部プロセスが誤って制御ファイルをデータファイルと誤認するリスクも減る。ロックファイル名はワークフォルダからの相対パスをそのまま使わず、そのハッシュ値（例: SHA-256）を用いる。深い階層のパスで Windows のパス長制限（MAX\_PATH=260 文字）に抵触するのを防ぐためである。

**ジャーナルの単位**: トランザクションごとに独立したジャーナルファイル（例: `.txfio/tx-{guid}.journal`）を持つ。複数プロセスから単一の共有ジャーナルへ同時追記すると別途排他制御が必要になり複雑化するため、ファイル作成自体がトランザクションの一意性を保証する方式にする。

**操作種別**: `PendingChangeKind` は Add = 0、Update = 1、Delete = 2、Move = 3、DeleteTree = 4、CreateDirectory = 5 である。欠番は置かない。ジャーナル文書の版は 1 のままである。種別が Attach の文書は読まず、移行もしない。JSON として読めないジャーナルは「読めないジャーナル」に従う。

**ジャーナルの上書き**: 初回の作成は `FileMode.CreateNew` で `.txfio/tx-{guid}.journal` へ直接書く。途中で落ちると、そのパスに不完全なファイルが残る。2 回目以降の上書きは、同じディレクトリの一時ファイル `.txfio/tx-{guid}.journal.tmp` に `WriteThrough` で書いて flush し、`File.Move(overwrite: true)` でジャーナルのパスへ置き換える。置き換えは同じディレクトリなので、古い完全な版か新しい完全な版のどちらかがジャーナルのパスに残る。一時ファイルへの書き込みに失敗したとき、および置き換えに失敗したときは、一時ファイルを消してから例外を返す。落ちて一時ファイルだけが残ったときは、そのジャーナルを処理する `RecoverAsync` が、生存ロックを開けたあと、読む前に一時ファイルを消す。一時ファイルはジャーナルではなく、残骸判定にも数えない。

**Move の連鎖**: 同じファイルの連続 Move（`Move(A→tmp)` のあと `Move(tmp→B)`）は `Move(A→B)` に畳む。一時名を挟んだ入れ替えは、この畳みで循環になるので支援しない。別ファイルの連鎖は畳まない。`Move(log.1→log.2)` と `Move(log→log.1)` のように、移動先が別の Move の移動元なら 2 件のまま残す。`log.2` のように空いている端が必要である。ステージでは、移動先が空いているか、既にこのトランザクションの別の Move の移動元であるときだけ受け付ける。呼び出しは空いている端から。ファイルもディレクトリも同じ。移動先が存在し、別の Move の移動元でもないときは `ExternalConflictException`。存在するファイルを `File.Move(overwrite: true)` で置き換える Move はしない。ファイルの Move の移動元への `Add` は受け付ける。ディスク上に移動元がまだあっても、追加対象が既にあるとはしない。ディレクトリの移動元への Add は `InvalidOperationException`。適用は、移動先が他の Move の移動元でない Move から始め、その移動元へ向かう Move を続ける。移動元への Add はその Move のあと。それ以外の順は今のまま。Before / After はこの適用順で投影する。落ちたあとの Recover も同じ順である。空いている端が無い循環はステージでは受け付けない。コミット前に端が無いと分かったときは `Failed` とし、実体には触れない。一般の依存グラフとトポロジカルソートは将来のまま。文字列と JSON の書き込みは「コミット後の姿」に合わせ、ファイル Move の移動元は Add になる

**コミット手順**（クラッシュ安全性の確保）

1. ジャーナルに `Committing` マーカーを書き込み fsync する。上書きは「ジャーナルの上書き」
2. 各ファイルへの確定処理を実行する。事前に、同一パスへの複数操作をジャーナル記録時点で正規化しておく（例: 同一パスへの`Add`の後に`Delete`が来た場合は両方を打ち消し合う。`Move(A→B)`の後に`Move(B→C)`が来た場合は`Move(A→C)`に畳み込む。`Move(A→B)`の後に`Update(B)`が来た場合は移動先への`Add`と元の`Delete`に畳む。`Move(A→B)`の後に`Delete(A)`または`Delete(B)`が来た場合は`Delete(A)`に畳む）。正規化後の操作について、非破壊的操作（Add新規作成、Move、CreateDirectory）を先に、次にUpdate、最後に Delete と DeleteTree（パスが深い順。ディレクトリの Delete はファイル Delete と Move 出しのあと非再帰。DeleteTree は `Directory.Delete(path, recursive: true)`）という単純な順序のみサポートする。ただし Move の連鎖は「Move の連鎖」の順で、移動元への Add はその Move のあとである。一般の依存グラフを考慮したトポロジカルソートは将来の拡張候補とする。各操作について、適用直前の Before と適用直後の After を `Committing` のジャーナルに書く。ファイルは存在・サイズ・最終更新日時（UTC）、無い状態はどちらも無し、ディレクトリは存在だけ。Move は元と先の両方。Add / Update / Delete / DeleteTree / Move の Before は検証時に取り、After はその時点で決まる（Add / Update は `.txnew`、Move の先は元ファイル、削除後と Move の元と DeleteTree の After は不在。DeleteTree の Before はディレクトリの存在）。同じジャーナルでは適用順に投影し、手前の After を次の Before にする。`CreateDirectory` の Before と After もディレクトリの存在だけで、中身は見ない。検証時に無い、またはファイルならコミットしない。適用は存在の確認だけで、rename しない。存在が Before にも After にも一致するときは適用済みとしてスキップし、作り直さない。適用後にジャーナルは更新しない。`Applying`/`Applied` は書かない（fsync は `Committing` の 1 回）
3. 全て完了したらジャーナルを削除しロックを解放

各操作は、対象パスの現在の状態を `Before`（未適用）・`After`（適用済み）と照合する。Add / Update は、`.txnew` が残り対象が Before と一致するときは未適用である。Before と After が同じでも `.txnew` を適用する。`.txnew` が無く対象が After と一致するとき、または `.txnew` が残っていても対象が Before と一致せず After と一致するときは、適用済みとして `.txnew` を消してスキップする。それ以外の操作は、マーカーがあり `After` と一致すれば `.txnew` を消してスキップし、`Before` と一致すれば再実行する。どちらとも一致しない場合は外部競合として扱う（「コミット時の外部干渉への対処」に従う）。文書が読めるとき、マーカーが無ければ未コミットとして安全にロールバックできる。操作の `.txnew`、ワークフォルダ配下の `.{guid}.txnew.prev`、`createdDirectories` の深い順の非再帰削除、および `CreateDirectory` の再帰削除である（「作成ディレクトリ」）。作成ディレクトリの削除に失敗したときは例外を再送出し、ジャーナルは残る。読めないときは「読めないジャーナル」に従う

**コミット時の外部干渉への対処**（ダーティリード許容の帰結として）

1. `Committing`マーカーを書き込む前に、ジャーナル内の全操作について前提条件を検証し、同時に Before / After を書く。Add の対象は無く、Update の対象はファイル、Delete の対象はファイルか直下の前提を満たすディレクトリ、DeleteTree の対象はディレクトリ、Move の元はファイルかディレクトリで先は無い。ステージ後に内容だけ変わった Update はここでは失敗にせず、Before はその時点のディスクにする。`CreateDirectory` もディレクトリが存在しなければ失敗し、ファイルにすり替わっていても失敗する。中身は見ない。前提が崩れていれば、`CreateDirectory` がすでに作ったディレクトリを除き、まだ実体には触れていないので、コミット全体を安全に中止し `Failed` を返す（「結果の詳細」）。失敗時の破棄は、未コミットの Dispose と同じく、そのディレクトリを中身ごと消す
2. 検証後・`Committing`マーカー書き込み後に適用を開始してから外部干渉が起きた場合（極めて稀）は、ロールフォワード原則により後戻りはできない。該当操作をスキップして続行し、`CommitAsync` の `CommitReport.Result` で明確に区別する（`Succeeded`：全操作が想定通り適用された／`PartialConflict`：一部操作で外部干渉による不整合が検出されたが確定はした。ジャーナルと、適用しなかった操作の `.txnew` を消してから返す／`Failed`：コミット前検証で失敗し実体には一切触れていない）。`PartialConflict`を明示的な列挙値にすることで、呼び出し側が戻り値を握りつぶしにくいAPI形状にする。拒んだ操作と飛ばした操作のパスと理由は「結果の詳細」に載せる。`Failed` のあと、同じトランザクションでもう一度コミットできる。`PartialConflict` のあと、同じインスタンスではやり直せない。共有違反の自動再試行はしない

**Recover API**: 自動では何も行わない。アプリ側がワークフォルダを開いたタイミングで明示的に `RecoverAsync()` を呼んだときのみ、`.txfio/` 内のジャーナルをスキャンし、stale と判定されたトランザクションを検出する。ジャーナルを一覧する前に、ワークフォルダの哨兵を排他で開く。手順は操作が取る排他の哨兵と同じで、自分以外の `.lock` に使用中があれば閉じて `LockContentionException`（`Path` はワークフォルダ）を返し、何も処理しない。哨兵が開けないときも同じ例外である。哨兵はすべてのジャーナルの処理が終わるまで持ち、閉じても `.lock` は消さない。`.txfio/` が無ければ哨兵を開かずに `NoPendingTransactions` を返す。stale とは、そのジャーナルの生存ロック `.txfio/tx-{guid}.lock` を `FileMode.OpenOrCreate`・`FileShare.None`・`FileOptions.DeleteOnClose` で開けることをいう。ファイルが無いとき（落ちたトランザクション、または生存ロック導入前の版が残したジャーナル）も、作って開けるので stale である。共有違反なら持ち主が生きている（同じプロセスでも別プロセスでもよい）ので、そのジャーナルは読まず、消さず、`.txnew` にも `CreateDirectory` のディレクトリにも触れずに飛ばす。共有違反以外の失敗はその例外のまま返す。開けたハンドルはそのジャーナルの処理が終わるまで持ち、ジャーナルを消したあとで閉じる。ロールフォワードで Before / After のどちらとも一致しない操作があったときも、残りの操作の適用を続け、適用しなかった操作の `.txnew` を消してからジャーナルを消す。結果は `ConflictDetected` で 1 回だけ伝え、次の `RecoverAsync` はそのジャーナルを処理し直さない。開けたあとにジャーナルが無ければ、持ち主が正常に終わったので何もせず閉じ、結果にも数えない。同じワークフォルダで `RecoverAsync` が同時に走っても、片方は共有違反で飛ばすので同じジャーナルを二重に処理しない。`Committing` マーカーの有無でロールフォワードかロールバックかをライブラリ側が判別して実行し、結果を返す。予期しないタイミングでファイルが書き換わることを避けるため、アプリが呼ぶまで動かない。一方、呼んだあとのロールフォワード／ロールバックの判断はデータ整合性上一意に決まるので、その判断自体はライブラリが自動で行う。書き込み系と同様に非同期 API とする。

**読めないジャーナル**: 生存ロックを開けたジャーナルを読み、JSON として解釈できないとき（`JsonException`、または逆シリアル化の結果が null）は読めないとする。ジャーナルは消さない。`CreateDirectory` で作ったディレクトリは文書が読めないので特定できず、残す。ファイル名が `.{guid}.txnew` で終わるファイルは、ワークフォルダ配下（`.txfio` を除く）から消す。比較は大文字小文字を区別しない。`guid` はジャーナルのファイル名から取る。他の読めるジャーナルは通常どおり処理する。1 件でも読めないジャーナルがあれば、戻り値は `JournalUnreadable` を優先する。ジャーナルが残るので、次の `BeginAsync` と `CommitAsync` は `RecoveryRequiredException` のままである。人がジャーナルを直すか消すまで解消しない。直したあとの `RecoverAsync` は、読めた内容でロールフォワードまたはロールバックする。消したあとは、残った `CreateDirectory` のディレクトリは未追跡の子として残る。読み取りが `IOException` のときは、その例外を再送出する。ジャーナルも `.txnew` も消さない。`RecoverReport` は返さない。それより前に処理したジャーナルは戻さない。残りのジャーナルは処理しない。哨兵は閉じる。

**ジャーナル存在に関する不変条件**: `.txfio/tx-{guid}.journal` が存在しないことは、そのトランザクションがコミット済み（またはそもそも開始されていない）であることを意味する。ジャーナル削除完了後・ロック解放前にクラッシュしても、OSが保持していたファイルロックは自動的に解放されるため安全（Recoverの対象外として扱ってよい）

**`RecoverResult`型**: `RecoverResult` は `CommitResult` とは別の列挙型である。`RecoverAsync()` の戻り値は `RecoverReport` で、この列挙値は `Result` に載せる（「結果の詳細」）。値は `NoPendingTransactions` = 0、`RolledBack` = 1、`RolledForward` = 2、`ConflictDetected` = 3、`JournalUnreadable` = 4 である。欠番は置かない。`ConflictDetected` は、外部干渉により Before / After のどちらとも一致しない操作が見つかり、そのジャーナルは消えていることを表す。`JournalUnreadable` は、JSON として読めないジャーナルが 1 件でもあったことを表す。そのジャーナルは残っている。複数の結果が重なるときは `JournalUnreadable`、`ConflictDetected`、`RolledForward`、`RolledBack` の順で 1 つだけ返す。`CommitAsync`はプロセスが生きたまま返す結果、`RecoverAsync()`は次回起動時に別プロセスが返す結果であり、意味的に異なるため型を分ける。生きているトランザクションのジャーナルは stale ではないので結果に数えない。飛ばしたジャーナルしか無ければ `NoPendingTransactions` を返す。飛ばしたことを表す値は足さない

**結果の詳細**: `CommitResult` と `RecoverResult` の列挙値は残す。`CommitAsync` の戻り値は `CommitReport`、`RecoverAsync` の戻り値は `RecoverReport` である。公開前なので、以前の列挙値そのものを返す形との互換は持たない。

`CommitReport` は `Result`（`CommitResult`）と `Operations`（`OperationReport` の一覧）を持つ。`Succeeded` のとき `Operations` は空である。`Failed` は検証で拒んだ操作だけ、`PartialConflict` は適用で飛ばした操作だけを、適用順に載せる。適用できた操作は載せない。

`OperationReport` はパス、`Move` の移動先（それ以外は null）、種別 `PendingChangeKind`、成り行き、理由を持つ。成り行きは `Rejected`（検証で拒んだ）と `Skipped`（適用で飛ばした）である。

理由 `OperationFailureReason` は次のとおり。欠番は置かない。

- `Missing` = 0。対象が無い
- `AlreadyExists` = 1。既にある
- `ReplacedByFile` = 2。ファイルにすり替わった
- `DirectoryPreconditions` = 3。ディレクトリの直下条件を満たさない
- `BeforeAfterMismatch` = 4。Before と After のどちらとも一致しない
- `SharingViolation` = 5。共有違反
- `IoFailure` = 6。それ以外の IO 失敗（`UnauthorizedAccessException` を含む）

検証で使うのは `Missing`、`AlreadyExists`、`ReplacedByFile`、`DirectoryPreconditions`。`.txnew` をファイルとして読めないときは、検証でも `IoFailure` にする。適用で使うのは `BeforeAfterMismatch`、`SharingViolation`、`IoFailure`。

`Failed` は実体に触れない（`CreateDirectory` がすでに作ったディレクトリを除く。失敗時の破棄は未コミットの Dispose と同じ）。ジャーナルは残り、コミット済みにはしない。同じトランザクションで、状態を直したあと `CommitAsync` を再度呼べる。`PartialConflict` はジャーナルと、適用しなかった操作の `.txnew` を消して確定する。同じインスタンスではやり直せない。共有違反で飛ばしたパスは、新しいトランザクションでやり直せる。`BeforeAfterMismatch` は、同じ書き込みを繰り返しても意図どおりには戻らない。ライブラリは共有違反を自動では再試行しない。

`RecoverReport` は `Result`（今の優先順位の `RecoverResult`）と `Journals`（`JournalReport` の一覧）を持つ。処理した順に載せる。`JournalReport` はトランザクション ID、そのジャーナルの `RecoverResult`、競合して飛ばした操作の一覧を持つ。競合が無いジャーナルと、読めないジャーナルの操作一覧は空である。生きているジャーナルは一覧に入れない。`ConflictDetected` の詳細はこの戻り値に載せ、ジャーナルは今どおり消す。次の `RecoverAsync` はそのジャーナルを処理し直さない。読み取りが `IOException` のときは、これまでどおり例外を再送出し、`RecoverReport` は返さない。

## スコープと非対応範囲

**対象操作**: ファイル・ディレクトリの Create/Update/Delete/DeleteTree/Rename・Move、ワークフォルダ内の Copy、ディレクトリの Import / Export、`CreateDirectory`、ZIP アーカイブの作成・Export・展開・Import。ディレクトリの `ReadAsync` と、ZIP 以外のアーカイブ形式（tar、GZip 単体、Brotli）は未対応

**Moveの制約**: 同一ボリューム内の移動のみサポート。別ボリューム（別ドライブ、別のファイルサーバー共有）への移動はエラーとする。ボリューム跨ぎのrenameはOSレベルでアトミックに保証されず、コピー＋削除相当の重い処理になる。この重い処理をライブラリが暗黙に実行してしまうと、ユーザーが気づかないうちに高コストな操作を実行することになるため、跨ぎたい場合は明示的な `ImportAsync`/`ExportAsync` を使わせる

**対象プラットフォーム**: Windows専用（NTFS/SMBファイルサーバー）からスタート。.NET上でのMove/Renameのatomicity保証やロック挙動はOS・ファイルシステムによって差異が大きいため、まずスコープを絞って設計を固める。クロスプラットフォーム拡張は将来の課題として余地を残す。Txfioが保証するクラッシュ安全性はWindows/NTFS上のファイルAPI・Flushセマンティクスに基づくものとし、SMBファイルサーバー側の内部的な永続化保証（サーバーキャッシュの扱い等）まではTxfioの責任範囲外とする

## 実装方針とプロジェクト構成

**ライブラリ名**: `Txfio`。当初`TxFs`を検討したが、NuGetに同名（パッケージIDは大文字小文字を区別しないため技術的に同一）の既存パッケージ`Txfs`（2019年8月最終更新、6年以上メンテナンスなし、同種の説明文）が存在するため変更した。`Txfio`自体はNuGetで完全一致する既存パッケージが見当たらない（近い名前の`EQXMedia.TxFileSystem`とは名前が異なる）

**プロジェクト構成**: 単一プロジェクトではなく複数プロジェクトに分割する（コアライブラリ、テストプロジェクトなど）。責務ごとにフォルダ/プロジェクトを分ける。

**テスト戦略**: 通常のユニットテストに加え、コミット途中で意図的に止めるテストを行う。テストだけが指定できるチェックポイントを仕込み、`Committing` を書いた直後（`AfterCommitting`）と、適用順で各操作が成功した直後（`AfterApply`）に止める。止めたあとの Dispose はロールバックせず、同じワークフォルダで `RecoverAsync` が実ファイルを復旧できることを検証する。`Applied` は書かない

**対象フレームワーク**: `net8.0` 単一ターゲット。.NET Standard 2.0 のような古い環境への対応は行わない。`net8.0` のパッケージは net8 / net9 / net10 のアプリから参照できる。ランタイム保証は Windows（NTFS/SMB）のみ。開発 SDK は .NET 10 でよい。`net8.0-windows` にはしない（Linux の SDK から参照できなくなるため）。`IAsyncDisposable` 前提の設計（非同期API、`await using`パターン）と整合する。

**ファイルシステムアクセスの抽象化**: ライブラリ内部の実装では`System.IO.Abstractions`のような抽象化層を挟まず、`System.IO`を直接使用する。このライブラリの価値の核（rename/コピーの原子性、ネットワークファイルシステム越しの実際の挙動）は抽象化層の裏でモックしても検証できないため。ただし公開APIはインターフェース化し（`ITransaction`等）、Txfioを**利用する側**がユースケース層のテストでモックできるようにする。Txfio自身の実装テストは実ファイルに対して行い、Txfioを使う側のテストはインターフェースのモックで行うという役割分担。

**NuGetパッケージ構成**: `Txfio`単一パッケージ。テスト用ヘルパーパッケージ（`Txfio.Testing`等）への分割は行わない。nuget.org への公開は GitHub リポジトリを public にしたあと（Phase 3 完了後）に行う。

**名前空間**: `Txfio`のフラット構成。個人名や会社名を冠したプレフィックスは付けない。

**API命名**: トランザクションを表す公開型は`ITransaction`（名前空間`Txfio`と組み合わせて`Txfio.ITransaction`として使う前提のため、型名自体に`WorkFolder`のような修飾語を重ねない）。エントリポイントは`Factory`のような専用クラスを挟まず、`Txfio`の静的メソッドとする：

```csharp
await using var tx = await Txfio.BeginAsync(path);
```
