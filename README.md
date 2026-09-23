# Txfio

Windows の共有フォルダ（NTFS / SMB）で、コミットするまで本物のファイルを変えず、落ちたら復旧できるファイル IO ライブラリです。

nuget.org にはまだありません。このリポジトリを参照してビルドします。TFM は `net8.0` です。SMB サーバー自身のキャッシュがいつディスクへ落ちるかは保証しません。

契約の正本は [`docs/design.md`](docs/design.md) です。

## はじめ方

ワークフォルダは既存のディレクトリです。フォルダを開くときに、先に復旧を呼びます。

```csharp
using Txfio;

await Txfio.RecoverAsync(@"D:\share\work");

await using ITransaction tx = await Txfio.BeginAsync(@"D:\share\work");
await tx.WriteAllTextAsync("a.txt", "hello");
CommitResult result = await tx.CommitAsync();
```

`CommitAsync` の前は、内容を同じディレクトリの `.txnew` に置きます。本物のパスは変えません。確定は rename です。`CommitAsync` を呼ばずに破棄すると、その `.txnew` とジャーナルは消えます。

`CommitResult` は例外ではありません。

| 値                | 意味                                                 |
| ----------------- | ---------------------------------------------------- |
| `Succeeded`       | 予定どおり適用した                                   |
| `PartialConflict` | 適用の途中で外部干渉があった。確定は進んでいる       |
| `Failed`          | 適用前の検証で失敗した。本物のパスはまだ変えていない |

落ちたジャーナルは、次の `RecoverAsync` が戻すか進めます。結果は `NoPendingTransactions` / `RolledBack` / `RolledForward` / `ConflictDetected` です。

## できないこと

- ディレクトリの `ReadAsync`。渡すと `UnsupportedOperationException` です
- ボリュームをまたぐ `MoveAsync`。コピーと削除には切り替えません。ファイルなら `ImportAsync` と `ExportAsync` を明示的に使います
- 複数ファイルが、外から見て一時点で揃うこと。コミット中は、一部だけ新しい状態が見えます
- コミット開始後の取り消し。適用が始まったら最後まで進め、落ちた分は `RecoverAsync` の対象です
- SQL や DTC と同じトランザクションへの参加
- 素のファイル API やエクスプローラーからの変更を防ぐこと。コミット前の `.txnew` も見えます
- Linux での動作保証

## 操作

呼んだ操作だけを追跡します。フォルダ全体の差分スキャンはしません。

| API                                        | できること                                                                                                                       |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| `AddAsync` / `UpdateAsync`                 | ファイルの新規作成と置き換え。内容は `.txnew` へ書く。進捗は `TransferProgress`                                                  |
| `DeleteAsync`                              | ファイル、または直下だけのディレクトリの削除予約。実削除はコミット時                                                             |
| `DeleteTreeAsync`                          | ディレクトリとその配下すべての削除予約。ステージでは木を走査せず、コミット時に再帰削除する                                       |
| `MoveAsync`                                | 同一ボリューム内のファイルまたはディレクトリの移動予約。ディレクトリはコミット時に 1 回 rename し、中身は付いていく              |
| `AttachAsync`                              | 外部が作ったファイルまたはディレクトリを、コピーも rename もせず取り込む。ファイルはサイズと最終更新日時。ディレクトリは存在だけ |
| `CopyAsync`                                | ワークフォルダ内のファイルまたはディレクトリをコピーする。コピー元は残す。ディレクトリはファイルごとの Add と空ディレクトリ      |
| `ImportAsync`                              | ワークフォルダの外のファイルまたはディレクトリを `.txnew` へコピーし、Add として残す。コピー元は消さない                         |
| `ExportAsync`                              | 読み取りと同じバイトを、ワークフォルダの外へコピーする。ディレクトリは配下の各ファイル。ジャーナルには残さず、ロックもしない     |
| `ReadAsync`                                | `.txnew` があればそれ、無ければ本物のファイル。ロックは取らない                                                                  |
| `ReadAllTextAsync` / `ReadAllLinesAsync`   | 読み取りと同じバイトを文字列、または行の配列にする                                                                               |
| `WriteAllTextAsync` / `WriteAllLinesAsync` | ディスク上に無ければ Add、あれば Update。省略した書きは BOM なし UTF-8                                                           |
| `ReadFromJsonAsync` / `WriteAsJsonAsync`   | `System.Text.Json`。書きは上と同じ Add / Update。オプション省略時は既定                                                          |
| `GetPendingChanges`                        | 未確定の操作一覧                                                                                                                 |
| `CommitAsync`                              | 検証してから rename と削除を適用する                                                                                             |
| `RecoverAsync`                             | 落ちたジャーナルを、マーカーの有無で戻すか進める                                                                                 |

書き込み系は非同期だけです。親ディレクトリの自動作成はしません。`CopyAsync` と、ディレクトリの `ImportAsync` / `ExportAsync` だけ、コピー先のディレクトリ自身とその空のサブディレクトリを作ります。メタデータフォルダ `.txfio` とその配下は操作できません。

変更系（Add / Update / Delete / DeleteTree / Move / Attach / Copy / Import）は、パスロックの前にワークフォルダの哨兵を共有で取り、トランザクションが終わるまで持ちます。ディレクトリ Move と `DeleteTreeAsync`、ディレクトリの `CopyAsync` と `ImportAsync` のあいだは、その哨兵は排他です。`ReadAsync` と `ExportAsync` はロックしません。このロックは Txfio を使う者同士の協調であり、素の `File` API は止めません。別のトランザクションが押さえていると、待たずに `LockContentionException` になります。

## コミットまでディスクは古いまま

ディスク上の `a.txt` が `old` のとき、`WriteAllTextAsync` は Update になります。読めるのは新しい内容で、ディスク上のファイルはコミットが成功するまで古いままです。

```csharp
await using ITransaction tx = await Txfio.BeginAsync(@"D:\share\work");
await tx.WriteAllTextAsync("a.txt", "new");
string text = await tx.ReadAllTextAsync("a.txt"); // "new"。ディスクの a.txt はまだ "old"
```

## ディレクトリ

`DeleteAsync` が見るのは直下だけです。空ならその 1 回で予約できます。直下に、このトランザクションの削除予約、Add の取り消し、ディレクトリの外への Move のどれでもない子があると、`ExternalConflictException` になり、メッセージは「ディレクトリの直下に未予約の子があります」です。直下だけを揃えて消すときは、深い方から予約します。

```csharp
await tx.DeleteAsync("tree/child/a.txt");
await tx.DeleteAsync("tree/child");
await tx.DeleteAsync("tree");
```

配下すべてを 1 回で消すときは `DeleteTreeAsync` です。ジャーナルはディレクトリ 1 件で、ステージでは木を走査しません。コミット時に再帰削除します。配下にこのトランザクションの操作があると `InvalidOperationException` です。ファイルを渡すと `UnsupportedOperationException` です。実行中は哨兵が排他です。中の残骸も消すので、呼び側は先に `RecoverAsync` します。ワークフォルダ自身は消せません。

`MoveAsync("tree", "archive")` はコミット時にディレクトリを 1 回 rename し、中身はジャーナルに書かずに付いていきます。移動元と移動先の配下への操作、自分自身の配下への移動は `InvalidOperationException` です。

`AttachAsync` はディレクトリも取り込めます。存在だけを記録し、子の変化は見ません。ロールバックでも消しません。そのディレクトリ自身の `DeleteAsync` は直下削除に、`DeleteTreeAsync` は全削除に、`MoveAsync` はディレクトリ Move に畳みます。ファイルの Attach は、サイズと最終更新日時がコミットまでに変わっていると `Failed` です。対象には触れません。

## コピー

`CopyAsync` はワークフォルダの中をコピーします。コピー元は残します。同じボリュームでもバイトをコピーし、rename やハードリンクにはしません。ファイルは Add です。ディレクトリは各ファイルが Add で、空のサブディレクトリも作ります。未コミットの Add は含まれません。ジャンクションとシンボリックリンクは辿りません。実行中は哨兵が排他です。コピー先が既にある、親が無い、同じパス、コピー先がコピー元の配下、予定された `DeleteTree` の配下は失敗します。失敗や取り消し、未コミットの破棄では作りかけを消します。コミットすると、作ったディレクトリは残ります。

`ImportAsync` は、ワークフォルダの外を同じ規則で取り込みます。コピー元は残します。ディレクトリのあいだは哨兵が排他で、ロックはコピー先だけです。

`ExportAsync` は外へ出します。ジャーナルには残さず、ロックもしません。`.txnew` があればその内容、無ければ本物です。成功したコピー先は破棄しても残ります。失敗や取り消しでは作りかけを消します。

## TxFileManager と SQLite

- **Txfio**（このライブラリ）
- **[TxFileManager](https://github.com/chinhdo/txFileManager)**（NuGet `TxFileManager`）。`System.Transactions` にファイル操作を参加させるライブラリです。Windows の TxF ではありません。TxF は Microsoft が非推奨としており、このライブラリは採用していません
- **データを SQLite に置く使い方**。ファイルツリーの代わりに、行や BLOB を 1 つのデータベースファイルへ入れます

### 有利なとき

本物のファイルを、遅いファイルサーバー上に残したいときです。コミットは再コピーではなく rename で、ディレクトリの移動は子を走査しません。ジャーナルはワークフォルダに残るので、プロセスが落ちても `RecoverAsync` で片付けられます。大きなディレクトリの移動や `DeleteTreeAsync` で、一時フォルダへの退避コピーを避けられます。

### 不利なとき

SQL の INSERT とファイル作成を、落ちても両方戻る 1 つのトランザクションにしたいときです。それは TxFileManager と `TransactionScope` の領域で、このライブラリは参加しません。ただしあちらは落ちたあとのファイル復旧がありません。

確定後も普通のファイルである必要がなく、検索や同時読み取りの一貫性が欲しいときは、SQLite の方が単純です。共有フォルダ上の既存ツールがファイルを直接開く必要があるなら、SQLite へ入れると利用者がファイルを扱えなくなります。

コミットの途中で他プロセスから見て中間状態が許されないときも向きません。複数ファイルの反映は 1 操作ずつです。`.txnew` が見えること、素のファイル API がロックを無視することも、許容できないなら向きません。

| 観点                      | Txfio                                                                                                         | TxFileManager                                                                    | SQLite にデータを置く                                             |
| ------------------------- | ------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| 確定後に残るもの          | 普通のファイルとディレクトリ                                                                                  | 普通のファイルとディレクトリ                                                     | データベースファイル。個別ファイルにはならない                    |
| いつ見えるか              | コミットまで本物のパスは旧状態。`.txnew` は見える                                                             | 呼んだ瞬間に反映される。分離は Read Uncommitted                                  | 他接続からは、コミット済みの状態が見える                          |
| クラッシュ                | ジャーナルが残る。`RecoverAsync` が戻すか進める                                                               | 復旧は揮発。落ちると途中のファイル操作が残る                                     | DB はジャーナルまたは WAL で復旧する。DB の外のファイルは含まない |
| 複数対象の揃い            | コミット中は一部だけ新しい。`Failed` は本物を変えない                                                         | 変更は即時に見える。複数ファイルの Move のロールバックが途中で止まった報告がある | DB 内の変更は 1 つのコミットに揃う                                |
| ディレクトリ全削除        | `DeleteTreeAsync`。ステージでは走査せず、コミット時に再帰削除。`DeleteAsync` は直下だけ                       | `DeleteDirectory`。実行時点で temp へ退避する                                    | パスを表す行を SQL で消す。ファイルツリーの削除ではない           |
| ディレクトリの取り込み    | `AttachAsync` は存在だけ。子の変化は見ない。ロールバックでも消さない                                          | ディレクトリを「作ったことにしない」専用 API は無い                              | 行として入れるならアプリが書く                                    |
| ディレクトリの移動        | 同一ボリュームで 1 回の rename。ボリューム跨ぎはエラー                                                        | `MoveDirectory`。即時。temp が別ボリュームだとコピーと削除になる                 | パス列の更新                                                      |
| 一時置き場                | 使わない。`.txnew` は同じディレクトリ                                                                         | 既定は `Path.GetTempPath()`                                                      | データベースファイル自身                                          |
| SMB 上の共有              | 対象。サーバーキャッシュの永続化は保証外                                                                      | 設計の主対象ではない                                                             | ネットワーク共有に置く使い方はサポート外                          |
| DB と同じトランザクション | 参加しない                                                                                                    | `TransactionScope` で参加できる                                                  | データベースの中だけ                                              |
| 同時実行                  | 利用者間はパス単位。ディレクトリ Move、全削除、ディレクトリコピー、ディレクトリの Import のあいだは哨兵が排他 | スレッドセーフと README にある。分離は Read Uncommitted                          | 書き込みは原則 1 接続。読み取りは WAL などで並行できる            |
| 素の File API             | 止められない                                                                                                  | 止められない                                                                     | DB を経由しない読み書きはトランザクションの外                     |
| プラットフォーム          | 保証は Windows                                                                                                | .NET Standard 2.0。Windows と Ubuntu でテストされている                          | クロスプラットフォーム                                            |
| API の形                  | 非同期のみ。コピーの進捗あり                                                                                  | 同期が中心                                                                       | `Microsoft.Data.Sqlite` なら同期と非同期                          |

TxFileManager の機能一覧は、公開 README と `DeleteDirectoryOperation`（ディレクトリを temp へ移し、ロールバックで戻し、確定後に temp を再帰削除する）に基づきます。作者は揮発エンリストだけをサポートし、プロセスが落ちると途中のまま残ると説明しています。SQLite をネットワークファイルシステム上に置くことは、SQLite 作者が確実な動作の対象外としています。

## ドキュメント

| 文書                                         | 役割                 |
| -------------------------------------------- | -------------------- |
| [`docs/design.md`](docs/design.md)           | 設計の正本           |
| [`docs/roadmap.md`](docs/roadmap.md)         | 実装順（Phase は仮） |
| [`docs/conventions.md`](docs/conventions.md) | コーディング規約     |
| [`CONTRIBUTING.md`](CONTRIBUTING.md)         | 進め方               |
| [`SECURITY.md`](SECURITY.md)                 | 脆弱性の報告         |

## ローカル検証

Windows では `./build.ps1` がゲートです。Linux ではこのスクリプトを実行せず、restore / format / build までにします。

```powershell
./build.ps1
```

## ライセンス

[MIT](LICENSE)
