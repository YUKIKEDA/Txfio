# Contributing to Txfio

[English](CONTRIBUTING.md) | 日本語

英語版 [`CONTRIBUTING.md`](CONTRIBUTING.md) が正本である。食い違うときは英語版に合わせてこのファイルを直す。

この文書は開発規約の**人間向け正本**です。エージェントのエントリポイントは [`AGENTS.md`](AGENTS.md)、強制ルールは `.cursor/rules/` です。コーディング規約の詳細は [`docs/conventions.md`](docs/conventions.md) です。設計の正本は [`docs/design.md`](docs/design.md)、実装順は [`docs/roadmap.md`](docs/roadmap.md) です。どの言語で書くかは [`docs/language.ja.md`](docs/language.ja.md) です。

`.dev/` は下書き専用です。決定事項を `.dev/` に残さないでください。

## 言語

リポジトリの言語は **英語** です（コード、コメント、テスト、ドキュメント、コミットメッセージ、Issue、PR）。`README`、`CONTRIBUTING`、`docs/` の主な文書には日本語訳（`*.ja.md`）もあります。どちらかを変えるときは、同じ PR で対になるファイルも変えてください。詳しくは [`docs/language.ja.md`](docs/language.ja.md) です。

## 必須ワークフロー

コードや規約に手を入れる作業は、**必ず次の順**で進める。Issue なし・ブランチなし・PR なしでの実装着手は禁止。

```text
（設計判断が残っていれば Grill）→ Issue 作成 →（実装不能なら再 Grill / チェックリスト化）→ ブランチ作成 → 作業 → PR → 人間レビュー → squash マージ → 繰り返し
```

1. **Grill**（Issue 作成の前）: API・意味論・フェーズ境界・例外方針など、判断が枝分かれするとき。スキルは [`.cursor/skills/grilling/SKILL.md`](.cursor/skills/grilling/SKILL.md)。受け入れ条件と非ゴールが Issue にあり、`docs/design.md` と矛盾しない作業では省略可
2. **Issue 作成**（テンプレ必須。空白 Issue は禁止）
3. 実装可能な粒度に落とせないときだけ **再 Grill**し、本文やチェックリストに書き戻す
4. **ブランチ作成:** `type/<issue号>-<slug>`（Issue 番号必須）
5. **作業**（Windows では `./build.ps1`、Linux では下の手順）
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
- コミット / PR タイトル: [Conventional Commits](.cursor/rules/conventional-commits.mdc)（英語で書く）
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
tests/Txfio.TestSupport/
tests/Txfio.Tests/
tests/Txfio.Stress/
docs/design.md
docs/roadmap.md
docs/conventions.md
docs/language.md
```

- ソリューション形式: **`.slnx` のみ**（`.sln` は使わない・置かない）
- ターゲット: **`net8.0`**（ランタイム保証は Windows）
- テスト: **xUnit**
- コーディング規約の詳細: [`docs/conventions.md`](docs/conventions.md)

## バージョン

SemVer。`0.x` は破壊的変更可。版の正本は git タグ（`v0.1.0` など）。

### リリース

リリースは `Release` ワークフロー（`.github/workflows/release.yml`）で公開する。タグ、GitHub Release、nuget.org の版を手で作らない。

変更は [`CHANGELOG.md`](CHANGELOG.md)（英語のみ、[Keep a Changelog](https://keepachangelog.com/en/1.1.0/)）に記録する。公開された振る舞いを変える PR は、次の版の日付の無い節に項目を足す。破壊的変更を先に、それぞれ移行のしかたを添えて書き、そのあとに追加、変更、修正を書く。

ワークフローを実行する前に、`main` にマージ済みの PR で、新しい版について次をすべて揃えておく。

- `src/Txfio/Txfio.csproj` の `VersionPrefix`
- `PackageReleaseNotes`: nuget.org のページ向けの短い要約。版で始め、`CHANGELOG.md` へのリンクを置く
- `CHANGELOG.md`: 日付の無い `## [{version}]` の節を 1 つと、末尾のリンク参照（`[{version}]: https://github.com/YUKIKEDA/Txfio/compare/v{previous}...v{version}`）

メンテナーが Actions タブから `main` で、版（`0.2.0`。`v` は付けない）を指定して `Release` を実行する。その時点の `main` のコミットで動き、最初の失敗で止まる。

1. `verify`: `.github/scripts/release-check.sh` が次を確かめる。
   - 版の形式とプロジェクトの版。
   - リリースノートの先頭。
   - 日付の無い changelog の節とそのリンク参照。
   - タグ `v{version}` とブランチ `release/v{version}` が無いこと。
   - nuget.org にその版が無いこと。
2. `test`: そのコミットで `build.yml` の両ジョブ（マージゲート）。
3. `pack`: そのコミットでの予行。`dotnet pack -c Release -p:ContinuousIntegrationBuild=true` を実行し、`.github/scripts/release-check-package.sh` が次を確かめる。
   - `.nuspec` の版、コミット、readme、リリースノート。
   - DLL と PDB のパスが決定的で、Source Link がそのコミットを指すこと。
4. `publish`（environment `release`）:
   - 実行した日の日付を changelog の見出しに入れたリリースコミット（`## [{version}] - YYYY-MM-DD`）を作る。そのコミットは、`main` のコミットの子として `release/v{version}` ブランチに載る。
   - そこに注釈付きタグ `v{version}` を作り、パックして確かめ、ブランチとタグを push する。
   - nuget.org の Trusted Publishing で短命の API キーを得て、nupkg を push する（snupkg も一緒に上がる）。
   - changelog の節を本文にし、両パッケージを添えた GitHub Release を作る。
   - リリースブランチで `build.yml` を起動し、日付入りの見出しを `main` に戻す PR を作る。その PR はメンテナーがマージする。

タグはパッケージより先に push する。タグは消せるが、nuget.org の版は消せない。nuget.org への push が失敗したら、タグとリリースブランチを消し、原因を直して、ワークフローをもう一度実行する。

リポジトリの持ち主が一度だけする設定:

- nuget.org › プロフィール › Trusted Publishing: 次の値のポリシー。
  - repository owner `YUKIKEDA`、repository `Txfio`、workflow file `release.yml`、environment `release`。
  - スコープは「Push only new package versions」、パッケージは `Txfio`。
  - 新しいポリシーは、最初の公開までは 7 日間だけ有効のことがある。
- GitHub › Settings › Environments: `release` という environment。そこに必須レビュアーを置くと、各実行が `publish` の前に承認待ちになる。
- GitHub › Settings › Actions › General: 「Allow GitHub Actions to create and approve pull requests」を有効にする。
- ルールセットでタグやブランチの作成を制限しているなら、GitHub Actions が `v*` タグと `release/v*` ブランチを作れるようにする。

## 検証

マージの関門は、**CI**（`.github/workflows/build.yml`）の両方のジョブが緑であること。

- `windows`: `./build.ps1` を実行するので、`WindowsFact` を含むすべての単体テストが走る
- `linux`: restore / format の確認 / build / test を直接実行する。`WindowsFact` は Skip になる

push の前に同じ確認をローカルで回すことは推奨で、必須ではない。

```powershell
./build.ps1
```

想定内容: `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test`。restore / format / build の対象は **`Txfio.slnx`**。`dotnet test` の対象は **`tests/Txfio.Tests/Txfio.Tests.csproj`**。耐久テスト（`tests/Txfio.Stress`）は関門に含めず、`dotnet test tests/Txfio.Stress/Txfio.Stress.csproj` で明示的に回す。

このスクリプトは **Windows 専用** です。Linux では `./build.ps1` を実行せず、`Txfio.slnx` に対して restore / format / build を順に回し、`dotnet test tests/Txfio.Tests/Txfio.Tests.csproj` を実行する。Linux では、`Windows only` として Skip されるもの（`WindowsFact`）以外の単体テストがすべて通ること。Skip されるものは CI の Windows ジョブが実行する。テストの実行には .NET 10 SDK に加えて .NET 8 ランタイムが要る。

クラッシュインジェクションと SMB 検証は、Issue の受け入れ条件に書いたときだけローカル必須とする。

PR の Verification では、CI の両方のジョブが緑であることを確かめ、ローカルで回したものがあれば書く。

## マージ

- **squash merge のみ**
- エージェントが書いた PR のマージ判断は人間が行う
