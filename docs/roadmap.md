# Roadmap

Phase の切り方と順序は **仮** である。実装順・境界は Grill と設計 PR で変えてよい。

意味論・API・非機能の正本は [`design.md`](design.md) である。このファイルは実装順の地図にすぎない。

## Phase 0 — リポジトリ基盤

規約・テンプレ・空ライブラリ・ローカルゲート。

- Issue #1: リポジトリ基盤（規約・テンプレ・空ライブラリ）

## Phase 1 — MVP（仮）

単一プロセス・単一トランザクション前提（協調ロック機構はスキップ）。`.txnew` ステージング、コミット、ロールバック、ジャーナル、クラッシュリカバリ（`Recover()`）を完成させ、クラッシュインジェクションテストで実ファイルシステム上で検証する。`tx.ReadAsync(path)` はこのフェーズのスコープ外とする。

## Phase 2 — 並行性（仮）

マルチプロセス対応。`.txfio/locks/` 下の `.lock` ファイル生成、`FileShare.None` による OS レベル排他ロック、Pessimistic な競合検知を実装する。

## Phase 3 — 拡張（仮）

ディレクトリ Move のサポート（ワークフォルダ全体排他ロックによる割り切り版）、`tx.ReadAsync`、進捗通知（`IProgress`）の結合テストなど。

## 公開（Phase 3 完了後）

- GitHub リポジトリを public にする
- nuget.org への publish（それまでは `dotnet pack` とメタデータのみ）
