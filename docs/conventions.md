# 開発規約

コードレビューで繰り返さないためのリポジトリ規約。人間向けの詳細正本である。要約は [`CONTRIBUTING.md`](../CONTRIBUTING.md)、強制は `.cursor/rules/` である。

## リポジトリ配置

- ライブラリは `src/Txfio/`、単体テストは `tests/Txfio.Tests/`、耐久テストは `tests/Txfio.Stress/`、両方が使う一時ディレクトリと `WindowsFact` は `tests/Txfio.TestSupport/`
- `.csproj` は各プロジェクトのルートに置く。リポジトリ直下にプロジェクトを並べない
- `.cs` は機能フォルダに置く。プロジェクト直下はエントリ（静的 `Txfio`）と実装の根と `.csproj` だけ。新しい機能は新しいフォルダを足し、直下へバラまかない
- テストは `src/Txfio/<Area>/` と同じ `<Area>/` を `tests/Txfio.Tests/` に作る。ファイル名は `Foo.cs` → `FooTests.cs`
- 名前空間はフォルダに連動させない。ライブラリは `Txfio`、単体テストと耐久テストは `Txfio.Tests`、共有ヘルパーは `Txfio.Tests.Support`
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

tests/Txfio.TestSupport/
  TempDirectory.cs         一時ディレクトリ。Fact は置かない
  WindowsFactAttribute.cs
tests/Txfio.Tests/
  TxfioTests.cs
  Support/                 単体テストのフィクスチャ。Fact は置かない
  Contracts/
  Journal/
tests/Txfio.Stress/
  耐久テスト。関門の dotnet test では回さない
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
- 文末にも文の途中にも **「。」や英語のピリオド `.` を付けない**（例外メッセージと README の地の文は対象外）
- 補足は（）、並列は、、状態の続きは「しており」でつなぐ。取り消しを「キャンセル」と書かない
- 設計用語の「哨兵」は、XML・README・テストの説明では「ワークフォルダ全体のロック」と書く。繰り返された言い換えは [`.cursor/skills/japanese-writing/SKILL.md`](../.cursor/skills/japanese-writing/SKILL.md)
- `src/` の XML コメントを足す・変える PR では、マージ前に **Gemini へ日本語の自然さをレビューさせる**（手順は `.cursor/rules/japanese-docs.mdc`）。新しい指摘は、文を直すのと同時にそのスキルへ一般則として足す

## 設計書と XML ドキュメントの分担

仕様の正本は `docs/design.md` とする。同じ規則を複数の場所に書くと、言い方が少しずつずれ、更新漏れの元になる。

### XML ドキュメント（`ITransaction` など公開面）に書くこと

- 利用者が呼ぶ前と呼んだ直後に要る契約だけを書く
  - 何をするか
  - 引数の意味
  - 投げる例外の型と、その条件の要約
  - 戻り値
- ロックの取り方、ジャーナルの書き方、畳み込み、後始末の順番など、実装と設計の詳細は書かない。必要なら「詳しくは設計書の〇〇」と参照する
- 設計書と食い違ったときは、設計書が正とする。XML ドキュメントを設計書に合わせて直す

### `docs/design.md` の API の節の形

API ごとに節を分け、次の見出しをこの順番で置く。当てはまらない見出しは「なし」と書き、省かない（書き漏れと区別するため）

| 見出し | 書くこと |
| --- | --- |
| 概要 | 何を予約するか。操作種別（`PendingChangeKind`） |
| 前提 | 呼べる状態、パスの条件、既存の予約との組み合わせ（畳み込みと拒否） |
| ロック | 取るロックと待ち方。横断する規則は「並行性とロック」を参照し、この API に固有のことだけを書く |
| ジャーナル | 書く行と、書く時点。横断する順序は「ジャーナルとリカバリ」を参照する |
| 例外 | 例外の型と条件の一覧（表） |
| 失敗時の後始末 | 途中で失敗したときに消すものと残るもの |

- ロックの順番、ジャーナルを書く順番、コミットの手順など、複数の API にまたがる規則は、それぞれの横断の節に 1 回だけ書く。API の節では繰り返さず参照する
- 規則を足すときは、まず横断の節か API の節のどちらに属するかを決め、1 か所にだけ書く

## テスト

- テストメソッド名は日本語の自然文にする。推奨形式は `{対象}_〜すると／したとき〜こと`
- 各 `[Fact]` / `[Theory]` には XML コメントで **前提**・**手順**・**期待** を残す。ヘルパーには不要
- `tests/Txfio.TestSupport/` に一時ディレクトリと `WindowsFact` を置く。Fact は置かない
- 単体テストのフィクスチャは `tests/Txfio.Tests/Support/` に置く。Fact は置かない
- テストごとに一意の一時ディレクトリを作り、破棄時に消す。並列実行を前提にする
- 乱数で約束を確かめる耐久テストは `tests/Txfio.Stress/` に置く。対応する `src/` のフォルダは無い
  - ランダム操作列はメモリ上のモデルと比べ、失敗したら手を外して縮めた列を出す。ファイルの Add / Update / Delete / Move / Read の列、ディレクトリの作成、空の削除、木の削除、上書きしない Move の列、移動先を置き換える Move の列、ファイルとディレクトリの Copy / Import / Export の列、文字列と JSON の読み書きの列、ZIP の作成と展開と取り込みと書き出しの列を持つ。ディレクトリの奪い合いは、少数を待たずに競合させる版と、多めを競合したらやり直す版の 2 つを持つ
  - 多プロセスの耐久は、耐久プロジェクトの実行ファイル自身を子プロセスとして起動する（`Program.cs` の入口）
  - 関門（`./build.ps1` と Linux の PR 前確認）の `dotnet test` は `tests/Txfio.Tests/Txfio.Tests.csproj` だけを実行する。耐久は `dotnet test tests/Txfio.Stress/Txfio.Stress.csproj` で明示的に回す。既定はその実行で短く終わる規模にする。長く、または大きく回すときは環境変数 `TXFIO_STRESS_SEED`、`TXFIO_STRESS_ITERATIONS`、`TXFIO_STRESS_PROCESSES`、`TXFIO_STRESS_FILES`、`TXFIO_STRESS_MAX_BYTES` で変える。失敗メッセージのシードを渡すと同じ列を再現できる
  - 多プロセスの耐久は、少数のファイルを待たずに奪い合う版（競合の組み合わせを増やす）と、多めのファイルを競合したらやり直す版（使い方に近い負荷）の 2 つを持つ。結果の件数はテストの出力に出る
  - 見つかった不具合はテストで隠さず、別 Issue にする

### Windows 専用のテストと Linux での実行

実行時に保証するのは Windows だけである（`docs/design.md`「対象プラットフォーム」）。ただし開発環境として、Linux でも単体テストの `dotnet test` を回せるようにしておく。

- Windows の挙動そのものに頼るテストは `[WindowsFact("理由")]`（`tests/Txfio.TestSupport/`）にする。対象は、ジャンクション、ドライブ文字のパス、開いたままのファイルを rename や削除できないこと。Windows 以外では `Windows 専用: 理由` として Skip になる
- パスの区切りはテストでも `/` か `Path.Combine` を使う。`\` は Linux ではファイル名の一部になる
- Linux では、PR を出す前に `dotnet test tests/Txfio.Tests/Txfio.Tests.csproj` を実行し、`WindowsFact` 以外のテストがすべて通っていること。耐久テストはこの確認に含めない。マージの関門は Windows の `./build.ps1` のままで、Windows では Skip は 0 件である
- ロック競合の判定（`PathLockSet.IsSharingViolation`）は、Linux の EAGAIN（`HResult` = 11）も共有違反とみなす。テストを回すためのもので、実行時の保証ではない。macOS は実機で確かめるまで足さない

## 書式

- 正本はリポジトリルートの `.editorconfig`
- file-scoped namespace、ImplicitUsings
- 変数の型は明示する。`var` は使わない
- コレクションと配列は `new List<T>()` と `new T[] { }` で書く。コレクション式 `[]`、型を省略した `new()`、オブジェクト初期化子への寄せ、プライマリコンストラクタは使わない
- `Substring` を範囲演算子や末尾からのインデックスに置き換えない。ラムダをメソッドグループに簡略化しない
- 名前空間はフォルダに合わせない（上の「リポジトリ配置」）。`Txfio.Archive` のようなフォルダ名の名前空間にはしない
- コールバックが末尾にある private メソッドでは、`CancellationToken` をコールバックの前に置く
- 公開メソッドの引数名を、ヘルパーから `ArgumentException` のパラメータ名として渡してよい
- `finally` でのロック復帰は、競合と取り消しだけを受け、それ以外の例外は呼び出し元へ届く
- 具象型への変更、インスタンスメソッドの static 化、引数の定数配列を static フィールドに出す、といった性能の提案ではコードを変えない
- 行末は **LF**（`.gitattributes` で固定）
- 提出前に `dotnet format` を通す。CI / `build.ps1` は `--verify-no-changes` で確認する
- コンパイラ警告はエラーにする（`TreatWarningsAsErrors`）
- 命名は **.NET の慣例**に合わせる
  - private フィールド（静的含む）は `_camelCase`
  - 定数は PascalCase
  - インスタンスメンバーに `this.` は付けない（識別子の衝突回避が必要なときだけ）
  - StyleCop の SA1101 / SA1306 / SA1309 / SA1310 / SA1311 は無効化する（上記と衝突するため）
- インターフェース名は `I` プレフィックス必須（SA1302 を無効化しない）
- ライブラリにログフレームワークを入れない。診断は戻り値と例外だけとする
