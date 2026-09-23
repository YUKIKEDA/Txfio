# Txfio

Windows（NTFS / SMB ファイルサーバー）向けの、git のステージングとコミットに着想を得たトランザクショナルなファイル IO ライブラリです。コミットするまで本物のパスは変えず、同じディレクトリの `.txnew` に内容を置きます。確定は rename です。落ちたトランザクションは、次回 `RecoverAsync` でロールバックまたはロールフォワードします。

契約の正本は [`docs/design.md`](docs/design.md) です。この README は、使う人が最初に判断するための説明です。

**現状**: 公開 API は実装済みです。GitHub は Phase 3 の公開作業まで private です。nuget.org への公開も同じタイミングです。それまではこのリポジトリを参照してビルドします。

## 対象

- TFM: `net8.0`（net8 以降のアプリから参照できます）
- ランタイム保証: Windows（NTFS / SMB）
- 一時フォルダは使いません。サイドカーは対象ファイルと同じディレクトリです

SMB サーバー自身のキャッシュがいつディスクへ落ちるかは、このライブラリの保証外です。

## はじめ方

ワークフォルダは既存のディレクトリです。アプリがフォルダを開くときに、先に復旧を呼びます。

```csharp
using Txfio;

RecoverResult recovered = await Txfio.RecoverAsync(@"D:\share\work");

await using ITransaction tx = await Txfio.BeginAsync(@"D:\share\work");
await using MemoryStream content = new MemoryStream("hello"u8.ToArray());
await tx.AddAsync("a.txt", content);
CommitResult result = await tx.CommitAsync();
```

`CommitAsync` を呼ばずに `Dispose` すると、そのトランザクションの `.txnew` とジャーナルは破棄されます。コミット前の本物のファイルは、このライブラリが書き換えていません。

`CommitResult` は例外ではありません。

| 値 | 意味 |
| --- | --- |
| `Succeeded` | 予定どおり適用した |
| `PartialConflict` | 適用の途中で外部干渉があった。確定は進んでいる |
| `Failed` | 適用前の検証で失敗した。本物のパスはまだ変えていない |

`RecoverResult` は `NoPendingTransactions` / `RolledBack` / `RolledForward` / `ConflictDetected` です。

## できること

操作はログです。呼んだものだけを追跡し、フォルダ全体の差分スキャンはしません。

| API | できること |
| --- | --- |
| `AddAsync` / `UpdateAsync` | ファイルの新規作成と置き換え。内容は `.txnew` へ書く。進捗は `TransferProgress` |
| `DeleteAsync` | ファイル、または条件を満たすディレクトリの削除予約。実削除はコミット時 |
| `MoveAsync` | 同一ボリューム内のファイルまたはディレクトリの移動予約。ディレクトリはコミット時に 1 回 rename し、中身はそれに付いていく |
| `AttachAsync` | 外部が作った既存ファイルを、コピーも rename もせず取り込む。サイズと最終更新日時を記録する |
| `ImportAsync` | ワークフォルダの外のファイルを `.txnew` へコピーし、Add として残す。コピー元は消さない |
| `ExportAsync` | 読み取りと同じバイトを、ワークフォルダの外の新しいファイルへコピーする。ジャーナルには残さない |
| `ReadAsync` | `.txnew` があればそれ、無ければ本物のファイル。ロックは取らない |
| `GetPendingChanges` | 未確定の操作一覧 |
| `CommitAsync` | 検証してから rename と削除を適用する |
| `RecoverAsync` | 落ちたジャーナルを、マーカーの有無で戻すか進める |

書き込み系は非同期だけです。親ディレクトリの自動作成はしません。メタデータフォルダ `.txfio` とその配下は操作できません。

変更系（Add / Update / Delete / Move / Attach / Import）は、パスロックの前にワークフォルダの哨兵を共有で取り、トランザクションが終わるまで持ちます。ディレクトリ Move のあいだだけ、その哨兵は排他です。`ReadAsync` と `ExportAsync` はロックしません。このロックは Txfio を使う者同士の協調であり、素の `File` API は止めません。

別のトランザクションが同じパス、またはディレクトリ Move 中のワークフォルダを押さえていると、待たずに `LockContentionException` になります。

## できないこと

- 1 回の呼び出しで、ディレクトリとその配下すべてを削除すること。詳細は次の節です
- ディレクトリの `AttachAsync` / `ImportAsync` / `ExportAsync` / `ReadAsync`。ディレクトリを渡すと `UnsupportedOperationException` です
- ボリュームをまたぐ `MoveAsync`。コピーと削除へは切り替えません。ファイルなら `ImportAsync` と `ExportAsync` を明示的に使います
- 複数ファイルにまたがる、外から見て一時点で揃う原子性。コミット中は、一部だけ新しい状態が見えます
- コミット開始後の取り消し。適用が始まったら最後まで進め、落ちた分は `RecoverAsync` の対象です
- SQL や DTC と同じトランザクションへの参加
- 素のファイル API やエクスプローラーからの変更を防ぐこと。コミット前の `.txnew` も見えます
- Linux での動作保証

## ディレクトリとその配下の削除

ありません。`DeleteAsync` が見るのはそのディレクトリの直下だけです。空ならその 1 回で予約できます。直下に、このトランザクションの削除予約、Add の取り消し、ディレクトリの外への Move のどれでもない子があると、`ExternalConflictException` になり、メッセージは「ディレクトリの直下に未予約の子があります」です。

木を消すときは、呼び出し側が深い方から予約します。次の木なら、ファイル、子ディレクトリ、親の順です。

```text
tree/
  child/
    a.txt
```

```csharp
await tx.DeleteAsync("tree/child/a.txt");
await tx.DeleteAsync("tree/child");
await tx.DeleteAsync("tree");
CommitResult result = await tx.CommitAsync();
```

コミット時の削除は深いパスからなので、予約が揃っていれば 1 回の `CommitAsync` で足ります。ワークフォルダ自身は消せません。

ディレクトリの Move は削除と違います。`MoveAsync("tree", "archive")` はコミット時にディレクトリを 1 回 rename し、中身はジャーナルに書かずに付いていきます。移動元と移動先の配下への操作、自分自身の配下への移動は `InvalidOperationException` です。

## ディレクトリの Attach

ありません。`AttachAsync` はファイル専用です。ディレクトリを渡すと `UnsupportedOperationException` で、メッセージは「ディレクトリの取り込みは未対応です」です。

ファイルの Attach は、外部プロセスがすでに置いたファイルを「Txfio が新規作成した」と誤ってロールバック時に消さないための API です。ファイルには触れません。Attach 時点のサイズと最終更新日時がコミット時に変わっていると、そのコミットは `Failed` です。

## TxFileManager と SQLite との比較

比べているのは次の 3 つです。

- **Txfio**（このライブラリ）
- **[TxFileManager](https://github.com/chinhdo/txFileManager)**（NuGet `TxFileManager`）。`System.Transactions` にファイル操作を参加させる .NET Standard 2.0 のライブラリです。Windows の TxF（Transactional NTFS）ではありません。TxF は Microsoft が非推奨としており、このライブラリは採用していません
- **データを SQLite に置く使い方**。ファイルツリーの代わりに、行や BLOB を 1 つのデータベースファイルへ入れ、SQL のトランザクションで確定します。SQLite 自体にディレクトリ削除 API がある、という意味ではありません

TxFileManager の機能一覧と「ディレクトリとその中身を消す」ことは、公開 README と `DeleteDirectoryOperation`（ディレクトリを temp へ移し、ロールバックで戻し、確定後に temp を再帰削除する）に基づきます。作者は揮発エンリストだけをサポートし、プロセスが落ちるとトランザクションが途中のまま残ると説明しています。SQLite をネットワークファイルシステム上に置くことは、SQLite 作者が確実な動作の対象外としています。

| 観点 | Txfio | TxFileManager | SQLite にデータを置く |
| --- | --- | --- | --- |
| 確定後に残るもの | 普通のファイルとディレクトリ | 普通のファイルとディレクトリ | データベースファイル。エクスプローラー上の個別ファイルにはならない |
| いつ見えるか | コミットまで本物のパスは旧状態。`.txnew` は見える | 呼んだ瞬間に反映される。実効的な分離は Read Uncommitted | 他接続からは、コミット済みの状態が見える |
| クラッシュ | ワークフォルダのジャーナルが残る。`RecoverAsync` が戻すか進める | 復旧の参加は揮発。落ちると途中のファイル操作が残る | データベースとしてはジャーナルまたは WAL で復旧する。DB の外に書いたファイルは含まない |
| 複数対象の揃い | コミット中は一部だけ新しい。検証に失敗した `Failed` は本物を変えない | 変更は即時に見える。複数ファイルの Move のロールバックが最初の失敗で止まり、一部だけ戻った報告がある | DB 内の変更は 1 つのコミットに揃う |
| ディレクトリ全削除 | 無い。直下だけ。子は呼び出し側が先に予約する | `DeleteDirectory` でディレクトリと中身を消す。実行時点で temp へ退避する | パスを表す行を SQL で消す。ファイルツリーの削除ではない |
| ディレクトリの取り込み | 無い。ファイルの `AttachAsync` だけ | 公開一覧にある Snapshot はファイル。ディレクトリを「作ったことにしない」専用 API は無い | 行として入れるならアプリが書く。ファイルシステム上のディレクトリではない |
| ディレクトリの移動 | 同一ボリュームで 1 回の rename。子は付いていく。ボリューム跨ぎはエラー | `MoveDirectory`。即時。temp が別ボリュームだとコピーと削除になる | パス列の更新 |
| 一時置き場 | 使わない。`.txnew` は同じディレクトリ | 既定は `Path.GetTempPath()`。別パスも指定できる | データベースファイル自身 |
| SMB 上の共有 | 対象。サーバーキャッシュの永続化は保証外 | 設計の主対象ではない | DB ファイルをネットワーク共有に置く使い方はサポート外 |
| DB と同じトランザクション | 参加しない | `TransactionScope` でデータベース操作と同じトランザクションに参加できる | データベースの中だけ |
| 同時実行 | Txfio 利用者間はパス単位。ディレクトリ Move 中はワークフォルダの哨兵が排他 | スレッドセーフと README にある。ファイルの分離は Read Uncommitted | 書き込みは原則 1 接続。読み取りは WAL などで並行できる |
| 素の File API | 止められない | 止められない | DB を経由しない読み書きはトランザクションの外 |
| プラットフォーム | 保証は Windows | .NET Standard 2.0。Windows と Ubuntu でテストされている | クロスプラットフォーム |
| API の形 | 非同期のみ。コピーの進捗あり | 同期が中心 | `Microsoft.Data.Sqlite` なら同期と非同期 |

### Txfio が有利なとき

本物のファイルを、遅いファイルサーバー上に残したいときです。コミットはデータの再コピーではなく rename で、ディレクトリの移動は子を走査しません。ジャーナルはワークフォルダに残るので、プロセスが落ちても `RecoverAsync` で片付けられます。一時フォルダへ木をコピーしないので、大きなディレクトリの削除予約や移動で、TxFileManager のような退避コピーを避けられます。削除そのものは直下ルールのままなので、大きな木を消すコストは呼び出し側の予約回数に残ります。

### Txfio が不利なとき

SQL の INSERT とファイル作成を、落ちても両方戻る 1 つのトランザクションにしたいときです。それは TxFileManager と `TransactionScope` の領域で、このライブラリは参加しません。ただしあちらは落ちたあとのファイル復旧がありません。

確定後も普通のファイルである必要がなく、検索や同時読み取りの一貫性が欲しいときは、SQLite の方が単純です。逆に、共有フォルダ上の既存ツールがファイルを直接開く必要があるなら、SQLite へ入れると利用者がファイルを扱えなくなります。

コミットの途中で他プロセスから見て中間状態が許されないときも、このライブラリは向きません。複数ファイルの反映は 1 操作ずつです。

エクスプローラーから `.txnew` が見えること、素のファイル API がロックを無視することも、許容できないなら向きません。

## ドキュメント

| 文書 | 役割 |
| --- | --- |
| [`docs/design.md`](docs/design.md) | 設計の正本 |
| [`docs/roadmap.md`](docs/roadmap.md) | 実装順（Phase は仮） |
| [`docs/conventions.md`](docs/conventions.md) | コーディング規約の詳細 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 進め方（人間向け正本） |
| [`AGENTS.md`](AGENTS.md) | エージェント入口 |
| [`SECURITY.md`](SECURITY.md) | 脆弱性の報告 |

## ローカル検証

正本ゲートは Windows 上の `./build.ps1` です。GitHub Actions は置くが、利用制限中は必須ゲートにしません。

```powershell
./build.ps1
```

Linux ではこのスクリプトを実行しないでください。restore / format / build までとし、テスト成功をマージ条件に使わないでください。

## ライセンス

[MIT](LICENSE)
