# Contributing to Txfio

この文書は開発規約の**人間向け正本**です。エージェントのエントリポイントは [`AGENTS.md`](AGENTS.md)、強制ルールは `.cursor/rules/` です。コーディング規約の詳細は [`docs/conventions.md`](docs/conventions.md) です。設計の正本は [`docs/design.md`](docs/design.md)、実装順は [`docs/roadmap.md`](docs/roadmap.md) です。

`.dev/` は下書き専用です。決定事項を `.dev/` に残さないでください。

## 必須ワークフロー

コードや規約に手を入れる作業は、**必ず次の順**で進める。Issue なし・ブランチなし・PR なしでの実装着手は禁止。

```text
（設計判断が残っていれば Grill）→ Issue 作成 →（実装不能なら再 Grill / チェックリスト化）→ ブランチ作成 → 作業 → PR → 人間レビュー → squash マージ → 繰り返し
```

1. **Grill**（Issue 作成の前）: API・意味論・フェーズ境界・例外方針など、判断が枝分かれするとき。スキルは [`.cursor/skills/grilling/SKILL.md`](.cursor/skills/grilling/SKILL.md)。受け入れ条件と非ゴールが Issue にあり、`docs/design.md` と矛盾しない作業では省略可
2. **Issue 作成**（テンプレ必須。空白 Issue は禁止）
3. 実装可能な粒度に落とせないときだけ **再 Grill**し、本文やチェックリストに書き戻す
4. **ブランチ作成:** `type/<issue号>-<slug>`（Issue 番号必須）
5. **作業**（Windows では `./build.ps1`）
6. **PR 作成**（テンプレ厳守。`## Related` に単独行で `Closes #N`）
7. **人間レビュー → squash マージ**
8. 次の Issue へ

- エージェントは Issue / ブランチ / PR を飛ばして実装を書き始めてはならない
- 設計に触れる PR をエージェント単独でマージしない
- ロードマップの予定項目は、GitHub Issue 化されるまで作業開始シグナルではない

### 基盤バッチ例外

最初のリポジトリ基盤（Issue #1）だけ、規約・テンプレ・空ライブラリを 1 Issue = 1 PR にまとめてよい。2 本目以降は通常の粒度に戻す。

## 外部貢献者

Grill はメンテナ側の道具です。外部の人は次に従ってください。

- **Issue を先に立ててから PR**する。ウォークイン PR（Issue なし）は受けない
- 設計に触れる提案は Issue で議論する。マージ判断と必要なら Grill はメンテナが行う
- 直接 push できるのはメンテナのみ（フォーク + PR）

## Issue と PR

- **1 Issue ≈ 1 PR**
- Issue タイプ: **feat** / **bug** / **task** / **design** / **spike**
- ブランチ名: `type/<issue号>-<slug>`（例: `feat/12-commit-rename`）
- コミット / PR タイトル: [Conventional Commits](.cursor/rules/conventional-commits.mdc)（type/scope は英語、subject は日本語可）
- 推奨 scope: `txfio`, `test`, `build`, `ci`, `docs`
- PR 本文は [`.github/pull_request_template.md`](.github/pull_request_template.md) の見出しを厳密に使用する
- **Issue の関連付け（必須）:** PR 本文の `## Related` に、GitHub が認識する Closing キーワードを**単独行**で書く（`Closes #12`）。箇条書きや URL だけは関連付けに失敗することがある

### ラベル

- `type:feat` / `type:bug` / `type:task` / `type:design` / `type:spike`
- `phase:0` … `phase:3`

## 設計変更プロセス

- **契約・公開 API・意味論・フェーズ境界・例外方針**に触れる変更は、先に `docs/design.md` を更新する **Design Issue + 設計 PR** をマージしてから実装 Issue を進める
- **誤字・表現の明確化・例示のみ**なら、実装 PR に設計 diff を含めてよい
- 「実装してから設計を後追い」は禁止
- 実装中に設計が必要になったら実装を止め、設計 PR を先に出す
- 書かれている設計は、その時点の意思決定である。提案するときは、その文を根拠に現状の形を守らない。いまの最適解を提案し、違うなら設計を変える。形を残すためだけに公開 API を足さない

## リポジトリ構成

```text
Txfio.slnx
src/Txfio/
tests/Txfio.Tests/
docs/design.md
docs/roadmap.md
docs/conventions.md
```

- ソリューション形式: **`.slnx` のみ**（`.sln` は使わない・置かない）
- ターゲット: **`net8.0`**（ランタイム保証は Windows）
- テスト: **xUnit**
- コーディング規約の詳細: [`docs/conventions.md`](docs/conventions.md)
- XML ドキュメントの日本語は、変更 PR で Gemini レビューする（[`.cursor/rules/japanese-docs.mdc`](.cursor/rules/japanese-docs.mdc)）

## バージョン

SemVer。`0.x` は破壊的変更可。版の正本は git タグ（`v0.1.0` など）。nuget.org への push は GitHub を public にしたあと（Phase 3 完了後）。

## ローカル検証（正本）

GitHub Actions の workflow はリポジトリに置くが、**利用制限により CI が動かないことがある**。マージ前のゲートは Windows 上の `./build.ps1` とする。Actions が動き始めたら CI 緑も必須にする（そのとき PR テンプレにチェックを足す）。

```powershell
./build.ps1
```

想定内容: `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test`。対象は **`Txfio.slnx`**。

このスクリプトは **Windows 専用** です。Linux では `./build.ps1` を実行せず、restore / format / build / `dotnet test` を順に回す。PR を出す前に、`Windows 専用` として Skip されるもの（`WindowsFact`）以外のテストがすべて通っていること。これは PR 前の条件で、マージの関門ではない。マージ前のテスト記録は Windows 側が必要です。テストの実行には .NET 10 SDK に加えて .NET 8 ランタイムが要る。

クラッシュインジェクションと SMB 検証は、Issue の受け入れ条件に書いたときだけローカル必須とする。

PR の Verification には、`./build.ps1` を実行した旨を書く。

## マージ

- **squash merge のみ**
- エージェントが書いた PR のマージ判断は人間が行う
