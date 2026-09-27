# 言語ポリシー

[English](language.md) | 日本語

英語版 [`language.md`](language.md) が正本である。食い違うときは英語版に合わせてこのファイルを直す。

nuget.org から Txfio を入れる人には、日本語を読まない人もいる。このページは、リポジトリの各部分をどの言語で書くか、日本語訳を英語の正本とどうそろえるかを決める。

常に適用するエージェント向けのルールは [`.cursor/rules/language.mdc`](../.cursor/rules/language.mdc) である。このリポジトリでの英語の書き方（用語、文の形）は [`.cursor/skills/english-writing/SKILL.md`](../.cursor/skills/english-writing/SKILL.md) にある。

## ルール

**リポジトリの言語は英語である。** コミットするものはすべて英語で書く。例外は下に挙げる日本語訳だけである。

| 対象 | 言語 |
| --- | --- |
| コード: 識別子、XML ドキュメント、インラインコメント、例外メッセージ | 英語 |
| テスト: メソッド名、Given / When / Then の remarks、コメント、アサーションと Skip のメッセージ | 英語 |
| `docs/`、`AGENTS.md`、`SECURITY.md`、`CODE_OF_CONDUCT.md` | 英語 |
| `.cursor/rules/`、`.cursor/skills/`、`.github/`（テンプレート、ワークフロー） | 英語 |
| コミットメッセージ、PR のタイトルと本文、Issue のタイトルと本文、レビューコメント | 英語 |
| 日英の 2 言語で持つ文書（下） | 英語の正本と日本語訳 |

チャットやメンテナとのやり取りは、どの言語でもよい。コミットするものと GitHub に投稿するものは、この表に従う。

## 日英の 2 言語で持つ文書

よく開かれる文書は、英語の正本の隣に日本語訳を置く。

| 英語（正本） | 日本語訳 |
| --- | --- |
| [`README.md`](../README.md) | [`README.ja.md`](../README.ja.md) |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | [`CONTRIBUTING.ja.md`](../CONTRIBUTING.ja.md) |
| [`docs/design.md`](design.md) | [`docs/design.ja.md`](design.ja.md) |
| [`docs/roadmap.md`](roadmap.md) | [`docs/roadmap.ja.md`](roadmap.ja.md) |
| [`docs/conventions.md`](conventions.md) | [`docs/conventions.ja.md`](conventions.ja.md) |
| [`docs/language.md`](language.md) | [`docs/language.ja.md`](language.ja.md) |

- 日本語のファイルは、同じフォルダに、拡張子の前に `.ja` を付けた同じ名前で置く
- どちらのファイルも先頭に言語の切り替え行を置く。英語のファイルは `English | [日本語](X.ja.md)`、日本語のファイルは `[English](X.md) | 日本語`
- **英語が正本である。** 2 つが食い違うときは英語が正しく、直すのは日本語のファイルである
- 一方の節をもう一方で探せるよう、見出しは同じものを同じ順に置く。コードブロック、識別子の表、リンクは両方で同じにする
- NuGet パッケージには `README.md`（英語）だけを入れる

### 2 言語の文書を足すとき

多くの読者が開く文書だけを上の表に足す。足すときは同じ PR で `docs/language.md` と `docs/language.ja.md` の両方に行を足す。ほかの文書は英語だけである。

## 訳をそろえる

- **同じ PR で両方のファイルを変える。** 2 言語の文書の英語を変える PR は日本語も変え、その逆も同じである
- 作者が日本語側を書けないときに限り、次の両方を満たせば日本語側を後回しにしてよい
  - PR 本文の `## Summary` にそう書く
  - `docs: sync <file>.ja.md` という題の後続 Issue を立て、`## Related` からリンクする
  日本語のファイルを、リリースをまたいで遅れたままにしない
- 日本語のファイルだけを変える PR（訳の改善）は意味を変えない。意味が変わるなら、先に英語のファイルを変える
- PR のチェックリストにこの項目がある。レビューでは両方のファイルが変わっていることを確かめる

## 日本語訳の書き方

- 語ではなく意味を訳す。常体で、自然な日本語にする
- 識別子、API 名、ファイルパス、コードは英語のまま残す
- 同梱のスキル [`.agents/skills/natural-japanese`](../.agents/skills/natural-japanese/SKILL.md) で言い回しを確かめられる
- 日本語の用語表（設計用語についてメンテナが選んだ言葉）は [`.cursor/skills/english-writing/SKILL.md`](../.cursor/skills/english-writing/SKILL.md) の「Terminology」にある

## 例外

日本語を書いてよいのは次だけである。

- 上に挙げた `*.ja.md`
- `.agents/skills/natural-japanese/`（第三者のスキル。`skills-lock.json` で管理し、手で直さない）
- このポリシーとルールのファイルで、例として日本語を出すところ（言語の切り替え行、用語表）
- わざと ASCII 以外にする必要があるテストデータ（たとえば Unicode の扱いを確かめるファイル名）。その横のコメントに理由を書く

これらの外にある日本語を探すには:

```bash
git ls-files | grep -v -E '\.ja\.md$|^\.agents/skills/natural-japanese/' \
  | LC_ALL=C.UTF-8 xargs grep -l -P '[\x{3040}-\x{30FF}\x{4E00}-\x{9FFF}]'
```

結果に出てよいのは、このポリシー、ルールのファイル、書き方のスキル、許されたテストデータだけである。

## 経緯

Issue #290 より前、リポジトリは日本語で書いていた。git の履歴、過去の Issue と PR は書き換えない。
