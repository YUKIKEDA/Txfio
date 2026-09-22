# 開発規約

コードレビューで繰り返さないためのリポジトリ規約。人間向けの詳細正本である。要約は [`CONTRIBUTING.md`](../CONTRIBUTING.md)、強制は `.cursor/rules/` である。

## リポジトリ配置

- ライブラリは `src/Txfio/`、テストは `tests/Txfio.Tests/`
- `.csproj` は各プロジェクトのルートに置く。リポジトリ直下にプロジェクトを並べない
- `.cs` は機能フォルダに置く。プロジェクト直下はエントリ（静的 `Txfio`）と実装の根と `.csproj` だけ。新しい機能は新しいフォルダを足し、直下へバラまかない
- テストは `src/Txfio/<Area>/` と同じ `<Area>/` を `tests/Txfio.Tests/` に作る。ファイル名は `Foo.cs` → `FooTests.cs`
- 名前空間はフォルダに連動させない。ライブラリは `Txfio`、テストは `Txfio.Tests`
- ソリューションは **`Txfio.slnx` をコミット**する。`.sln` は置かない

公開面は契約（`Txfio`、`ITransaction`、結果型、例外、進捗）だけとする。実装型は `internal` とする。テストアセンブリへ `InternalsVisibleTo` を付ける。

## 機能フォルダ

プロジェクト直下に機能ファイルを足さない。追加先が無いときはフォルダを新設する。例:

```text
src/Txfio/
  Txfio.cs                 エントリ（BeginAsync / Recover）
  Contracts/               公開契約（インタフェース・例外・結果型）
  Journal/
  Staging/
  Commit/
  Recover/

tests/Txfio.Tests/
  TxfioTests.cs
  Support/                 TempDirectory など。Fact は置かない
  Contracts/
  Journal/
```

## partial クラス

既定は **1 型 1 ファイル**。ファイルが長いだけでは partial にしない。ヘルパーは別型へ切り出す。

トランザクション実装型だけ、機能フォルダに対応する partial を許す。

- 機能フォルダには `Type.<Area>.cs` を **1 つだけ** 置く。同じフォルダに 2 枚目は足さない
- テストクラスは partial にしない

## using エイリアス

- `using IoFile = System.IO.File;` のような型エイリアスは、記述短縮のためには使わない
- BCL の `File` / `Directory` と衝突する場合は `System.IO.File` / `System.IO.Directory` と完全修飾する

## 非同期と所有権

- ライブラリ内の `await` は `ConfigureAwait(false)` する
- `CancellationToken` は最後の引数にする
- メソッドが受け取った `Stream` は **呼び出され側が Dispose しない**（所有権は呼び出し側）。自分で開いたハンドルだけ閉じる

## パス

内部では正規化した絶対パスで比較し、ロック取得順を決める。パス比較は `OrdinalIgnoreCase` とする。

## 公開 API の XML コメント

- `public` な型・メンバーには XML ドキュメントコメントを付ける（`<summary>` 必須。引数・戻り値・例外は読み手が迷うものに付ける）
- **internal 型でも `public` メンバーには付ける**（例: 実装型の `ITransaction` メンバー、JSON 用の public コンストラクタ）
- `internal` な型・メンバーも付ける（StyleCop `documentInternalElements`）。テストと `private` は必須にしない
- 実装がインタフェースと同じ契約なら `/// <inheritdoc />` を使う。重複して書き直さない
- ライブラリプロジェクトは `GenerateDocumentationFile=true`。欠落は CS1591 としてエラーにする
- テストプロジェクトは XML ドキュメントを生成しない（CS1591 は出さない）
- XML ドキュメントおよび通常コメントは **日本語** とする（識別子・型名・公開 API 名は英語のまま）
- 文末に **「。」や英語のピリオド `.` を付けない**（例外メッセージなどユーザー向け文言は対象外）
- `src/` の XML コメントを足す・変える PR では、マージ前に **Gemini へ日本語の自然さをレビューさせる**（手順は `.cursor/rules/japanese-docs.mdc`）

## テスト

- テストメソッド名は日本語の自然文にする。推奨形式は `{対象}_〜すると／したとき〜こと`
- 各 `[Fact]` / `[Theory]` には XML コメントで **前提**・**手順**・**期待** を残す。ヘルパーには不要
- `tests/Txfio.Tests/Support/` に一時フォルダヘルパーを置く。Fact は置かない
- テストごとに一意の一時ディレクトリを作り、破棄時に消す。並列実行を前提にする

## 書式

- 正本はリポジトリルートの `.editorconfig`
- file-scoped namespace、ImplicitUsings
- 行末は **LF**（`.gitattributes` で固定）
- 提出前に `dotnet format` を通す。CI / `build.ps1` は `--verify-no-changes` で確認する
- コンパイラ警告はエラーにする（`TreatWarningsAsErrors`）
- インターフェース名は `I` プレフィックス必須（SA1302 を無効化しない）
- ライブラリにログフレームワークを入れない。診断は戻り値と例外だけとする
