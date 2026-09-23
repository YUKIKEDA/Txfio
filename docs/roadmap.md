# Roadmap

Phase の切り方と順序は **仮** である。実装順・境界は Grill と設計 PR で変えてよい。

意味論・API・非機能の正本は [`design.md`](design.md) である。このファイルは実装順の地図にすぎない。

チェックは **main にマージ済み** のときだけ付ける。PR 中は Issue 番号だけ書く。

## いまどこか

| 区間 | 状態 |
| --- | --- |
| Phase 0 リポジトリ基盤 | 完了 |
| Phase 1 MVP | 進行中（開始・Add/Update/Delete/Move/Attach・ディレクトリ削除・同一パス正規化・適用順済み。次は Before/After Issue #21） |
| Phase 2 並行性・ロック | 未着手 |
| Phase 3 拡張と公開 | 未着手 |

Phase 1 の「使えるライブラリ」までを操作で見ると、**Add / Update / Delete / Move / Attach**、ディレクトリ削除、同一パス正規化、適用順は main にある。次はジャーナルの Before / After（Issue #21）。そのあとカスタム例外とクラッシュインジェクションが残る。

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
- Issue #21: Before / After（ファイルはサイズと最終更新日時、ディレクトリは存在だけ。Recover はそれで適用済み判定）
- [ ] カスタム例外（`ExternalConflictException` など。ロック競合は Phase 2）
- [ ] クラッシュインジェクション（`Committing` 直後、Move / Delete 直後など）と実 FS 上の Recover 検証

## Phase 2 — 並行性（仮）

- [ ] `.txfio/locks/` のハッシュ名 `.lock` を `FileShare.None` で保持
- [ ] 操作時点でロック取得。取れなければ即例外（待機しない）
- [ ] `Move` は旧パス・新パスを辞書順でロック
- [ ] Recover はジャーナル記載パスから lock を特定してハンドルを閉じる（ワークフォルダ全スキャンはしない）
- [ ] 同一プロセス複数 Tx、複数プロセスからの同時アクセスのテスト

## Phase 3 — 拡張（仮）

- [ ] `tx.ReadAsync`（ステージング済みなら `.txnew`、なければ本物）
- [ ] `IProgress<TransferProgress>` を書き込み API に足し、結合テストする
- [ ] `ImportAsync` / `ExportAsync`
- [ ] ディレクトリ Move（実行中はワークフォルダ全体ロック）

## 公開（Phase 3 完了後）

- [ ] GitHub リポジトリを public にする
- [ ] nuget.org へ publish（それまでは `dotnet pack` とメタデータのみ）
