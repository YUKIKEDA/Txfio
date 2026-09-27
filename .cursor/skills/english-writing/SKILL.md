---
name: english-writing
description: >-
  Write Txfio comments, XML docs, test names and Given/When/Then remarks, exception messages, and docs in English, with the repository's terminology.
  Use when writing or editing XML docs, inline comments, test names or remarks, exception messages, README or design wording,
  when translating between the English docs and their *.ja.md translations,
  or when a human or bot review flags wording.
---

# Writing English in Txfio

Read this before writing. When a review corrects wording, add the general rule here in the same change. Do not leave it in chat. Do not repeat a finding that is already written here.

Do not carry contract terms from the design into text for users as they are. Design changes go through a design PR first; this skill decides the words in comments and docs.

The language policy (which files are English, which are bilingual) is [`docs/language.md`](../../../docs/language.md).

## Sentence shape

- Plain, direct English. Prefer short sentences and common words; many readers are not native speakers
- XML doc elements are full sentences and end with a period (StyleCop SA1629)
- Summaries start with a verb in the third person: "Stages ...", "Returns ...", "Deletes ..."
  - Properties: "Gets ..." or "Gets or sets ..." (SA1623)
  - Constructors: "Initializes a new instance of the <see cref="T"/> class." (SA1642)
  - Boolean members: "Gets a value indicating whether ..."
- Write `<see langword="true"/>`, `<see langword="false"/>`, `<see langword="null"/>` for keywords in XML docs
- Refer to types and members with `<see cref="..."/>` and parameters with `<paramref name="..."/>`
- Keep the subject the same across a method name, its summary, and its remarks
- State the actual check, not a broader phrase: "differs from the recorded size or last write time", not "changed externally"
- Name every place an exception is thrown; do not describe a narrower set than the code
- Inline comments explain why, not what the next line does

## Test names and remarks

- Method names: `{Target}_{Behavior}` in PascalCase, for example `CommitAsync_RollsBackWhenPreconditionFails`
  - `{Target}` is the method or type under test
  - `{Behavior}` states the expected behavior, usually in the present tense ("Throws...", "Keeps...", "Returns...")
  - Keep names readable. Do not abbreviate. A long name is fine
- Remarks use three paragraphs:

  ```csharp
  /// <remarks>
  /// <para>Given: a file a.txt exists in the work folder.</para>
  /// <para>When: the transaction deletes a.txt and commits.</para>
  /// <para>Then: a.txt is gone and the journal is deleted.</para>
  /// </remarks>
  ```

- In Given / Then, write the relative path that the test actually uses (`sub/a.txt`), and the actual check ("the work folder has no `.txnew`")

## Exception messages

- One sentence, no period at the end, then `: ` and the path when there is one: `"The work folder does not exist: " + workFolder`
- Use the same terms as the design and the XML docs
- Do not lump "the folder itself" and "what is under it" together as "in the folder"; say which

## Terminology

Use these words. The left column is the design or Japanese concept; the right is the English to write everywhere (XML docs, README, tests, design).

| Concept | Write |
| --- | --- |
| 哨兵 (the lock on the whole work folder, relative path `.`) | work-folder lock (not "sentinel") |
| ワークフォルダ | work folder |
| パスロック | path lock |
| 意図ロック | intent lock |
| しるし (`share-lost.lock`) | share-lost marker |
| 生存ロック (`tx-{guid}.lock`) | liveness lock |
| 残骸ジャーナル | orphaned journal |
| 読めないジャーナル | unreadable journal |
| ステージングファイル、サイドカー (`.txnew`) | staging file (`.txnew`); "sidecar" only in the design's write model |
| 再ステージの退避 (`.prev`) | restage backup (`.prev`) |
| 入れ替えの退避 (`.txold`) | swap backup (`.txold`) |
| 作成ディレクトリ (`createdDirectories`) | created directories |
| コミット後の姿 | post-commit view |
| 予約する | schedule (an operation); stage (a file's content) |
| 畳み込み、畳む | folding, fold into |
| 適用 | apply |
| 確定 | finish (the commit) |
| 破棄 | discard |
| 取り消し | cancellation, cancel |
| 直下 | direct children |
| 配下 | under (a directory), descendants |
| 置き換え (file over file) | replace, replacement |
| 入れ替え (a directory is involved) | swap |
| Move の連鎖 | Move chain |
| 空いている端 | free end |
| 外部変更 | external change |
| 外部干渉 | external interference |
| 素のファイル API | plain file API |
| 短縮名 (8.3) | short name |
| 長い名前 | long name |
| 共有違反 | sharing violation |
| 耐久テスト | stress test |
| 関門 | gate |
| 成り行き (`OperationDisposition`) | disposition |
| 理由 (`OperationFailureReason`) | reason |

Words to avoid:

| Avoid | Write |
| --- | --- |
| sentinel (outside the history of the design) | work-folder lock |
| "the metadata" for a file's ACL and attributes | list them: "the ACL, attributes, and creation time" (keep "metadata folder" for `.txfio`) |
| "fails" for throwing | "throws `XException`" |
| "applied" for something only scheduled | "scheduled" or "staged" |
| "in the folder" when it matters whether the folder itself is included | "the folder itself" / "under the folder" |
| "index" for a position in a table | "position" |
| "file operation" mixed with "disk operation" | "disk operation" |

## Writing the Japanese translations

The Japanese `*.ja.md` files use the Japanese column of the terminology table above. Use plain form (常体), natural Japanese, and keep identifiers and code in English. The vendored skill [`.agents/skills/natural-japanese`](../../../.agents/skills/natural-japanese/SKILL.md) can check the wording.

## After a review

1. Before applying a suggested replacement, look for similar sentences elsewhere
2. If the finding is not covered above, add it here as a general rule, not as one sentence
3. Then run the build
