# Roadmap

Phase の切り方と順序は **仮** である。実装順・境界は Grill と設計 PR で変えてよい。

意味論・API・非機能の正本は [`design.md`](design.md) である。このファイルは実装順の地図にすぎない。

チェックは **main にマージ済み** のときだけ付ける。PR 中は Issue 番号だけ書く。

## いまどこか

| 区間 | 状態 |
| --- | --- |
| Phase 0 リポジトリ基盤 | 完了 |
| Phase 1 MVP | 完了 |
| Phase 2 並行性・ロック | 完了 |
| Phase 3 拡張と公開 | 進行中（次はディレクトリ Attach Issue #47） |

Phase 3 の実装（Issue #33、#35、#37、#39、#45）と利用者向け README（Issue #41）、設計（Issue #43）は main にある。次はディレクトリの Attach（Issue #47）。そのあと公開（リポジトリ public と nuget.org）。

## Phase 0 — リポジトリ基盤

- [x] Issue #1: 規約・テンプレ・空ライブラリ・`./build.ps1`

## Phase 1 — MVP（仮）

単一プロセス・単一トランザクション。協調ロックはスキップ。`tx.ReadAsync` はこのフェーズの外。

### ライフサイクルと書き込み

- [x] Issue #3: `BeginAsync` / 空の `CommitAsync` / 未コミット Dispose / `RecoverAsync`（操作なし）
- [x] Issue #5: `AddAsync` / `UpdateAsync`、`.txnew`、コミット時 `File.Move`、Recover の sidecar
- [x] Issue #7: `DeleteAsync`（予約のみ、コミット時に実削除。ディレクトリ削除は含めない）
- [x] Issue #9: ファイルの `MoveAsync`（同一ボリュームのみ。ディレクトリ Move は Phase 3）
- [x] Issue #11: `AttachAsync`（ファイルは触らずジャーナル登録。サイズ・更新日時を期待状態に記録）
- [x] Issue #13: ディレクトリ削除の意味論（`DeleteAsync` を直下のみ・暗黙の巻き込みなしに固定）
- [x] Issue #15: ディレクトリの `DeleteAsync`（直下のみ、コミット時に非再帰削除）

### ジャーナル・コミット・Recover の完成

- [x] Issue #17: 同一パス／依存の正規化の残り（Move 先への Update / Delete、Move 元への Delete。Attach のあと Update / Delete / Move は #11）
- [x] Issue #19: 適用順の固定（Add / Move / Attach → Update → Delete。ディレクトリ Delete は深いパスから）
- [x] Issue #21: Before / After（ファイルはサイズと最終更新日時、ディレクトリは存在だけ。Recover はそれで適用済み判定）
- [x] Issue #23: カスタム例外（`ExternalConflictException` と `UnsupportedOperationException`。ロック競合は Phase 2）
- [x] Issue #25: クラッシュインジェクション（`AfterCommitting` と `AfterApply`。Dispose はロールバックせず Recover で実ファイルを検証）

## Phase 2 — 並行性（仮）

- [x] Issue #27: 操作時点のパスロック（`.txfio/locks/`、Move は辞書順、同一プロセスの2トランザクション。ファイルは消さない）
- [x] Issue #29: Recover は `.lock` を開かず消さない（クラッシュ後もファイルは残り、別トランザクションが取り直せる）
- [x] Issue #31: 別プロセスからの同時アクセス（保持中の競合、別パス、Dispose せず終了したあとの取り直し。SMB は含まない）

## Phase 3 — 拡張（仮）

- [x] Issue #33: `ReadAsync`（`.txnew` があればそれ、無ければ本物。ロックは取らない）
- [x] Issue #35: Add / Update のコピー進捗（`TransferProgress`。退避とジャーナルは通知しない。コミット件数は含めない）
- [x] Issue #37: Import / Export（外からのコピー＋Add、外への読み取りコピー。コピー元は消さない）
- [x] Issue #39: ディレクトリ Move（1 回の rename。実行中はワークフォルダの哨兵を排他）
- [x] Issue #41: 利用者向け README（できること、できないこと、TxFileManager と SQLite との比較）
- [x] Issue #43: 設計: 全削除、ディレクトリ Attach、Copy、ディレクトリの Import / Export（実装は含めない）
- [x] Issue #45: `DeleteTreeAsync`（配下すべての削除予約。ステージでは木を走査せず、コミット時に再帰削除。実行中は哨兵を排他）
- Issue #47: ディレクトリの Attach（存在だけ。子の変化は見ない。ロールバックでも消さない）

## 公開（Phase 3 完了後）

- [ ] GitHub リポジトリを public にする
- [ ] nuget.org へ publish（それまでは `dotnet pack` とメタデータのみ）
