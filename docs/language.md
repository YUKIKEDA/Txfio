# Language policy

English | [日本語](language.ja.md)

People who install Txfio from nuget.org include readers who do not read Japanese. This page decides which language each part of the repository uses, and how the Japanese translations stay in sync with the English source.

The always-applied agent rule is [`.cursor/rules/language.mdc`](../.cursor/rules/language.mdc). How to write English in this repository (terminology, sentence shape) is in [`.cursor/skills/english-writing/SKILL.md`](../.cursor/skills/english-writing/SKILL.md).

## Rule

**English is the language of the repository.** Everything that is committed is written in English, except the Japanese translations listed below.

| Area | Language |
| --- | --- |
| Code: identifiers, XML docs, inline comments, exception messages | English |
| Tests: method names, Given / When / Then remarks, comments, assertion and skip messages | English |
| `docs/`, `AGENTS.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md` | English |
| `.cursor/rules/`, `.cursor/skills/`, `.github/` (templates, workflows) | English |
| Commit messages, PR titles and bodies, Issue titles and bodies, review comments | English |
| Bilingual documents (below) | English source + Japanese translation |

Conversations in chat, or with the maintainer, may use any language. What is committed or posted on GitHub follows this table.

## Bilingual documents

The documents people open most often have a Japanese translation next to the English source.

| English (source of truth) | Japanese translation |
| --- | --- |
| [`README.md`](../README.md) | [`README.ja.md`](../README.ja.md) |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | [`CONTRIBUTING.ja.md`](../CONTRIBUTING.ja.md) |
| [`docs/design.md`](design.md) | [`docs/design.ja.md`](design.ja.md) |
| [`docs/roadmap.md`](roadmap.md) | [`docs/roadmap.ja.md`](roadmap.ja.md) |
| [`docs/conventions.md`](conventions.md) | [`docs/conventions.ja.md`](conventions.ja.md) |
| [`docs/language.md`](language.md) | [`docs/language.ja.md`](language.ja.md) |

- The Japanese file has the same name with `.ja` before the extension, in the same folder
- Both files start with a language switch line: `English | [日本語](X.ja.md)` in the English file, `[English](X.md) | 日本語` in the Japanese file
- **English is the source of truth.** When the two disagree, the English file wins, and the Japanese file is the one to fix
- Both files keep the same headings in the same order, so that a section in one can be found in the other. Code blocks, tables of identifiers, and links are the same in both
- The NuGet package uses `README.md` (English) only

### Adding a bilingual document

Add a document to the table above only when many readers open it. Add the row in the same PR in both `docs/language.md` and `docs/language.ja.md`. Every other document is English only.

## Keeping translations in sync

- **Change both files in the same PR.** A PR that changes a bilingual English file also changes its Japanese file, and the other way round
- If the author cannot write the Japanese side, the PR may leave it out only when:
  - the PR body says so under `## Summary`, and
  - a follow-up Issue titled `docs: sync <file>.ja.md` is opened and linked in `## Related`
  The Japanese file must not stay behind across a release
- A PR that changes only a Japanese file (a better translation) must not change the meaning. If the meaning changes, change the English file first
- The PR checklist has an item for this; reviewers check that both files changed

## Writing the Japanese translations

- Translate the meaning, not the words. Use plain form (常体) and natural Japanese
- Keep identifiers, API names, file paths, and code as they are in English
- The vendored skill [`.agents/skills/natural-japanese`](../.agents/skills/natural-japanese/SKILL.md) can check the wording
- The Japanese terminology table (the words the maintainer chose for design terms) is at the end of [`.cursor/skills/english-writing/SKILL.md`](../.cursor/skills/english-writing/SKILL.md)

## Exceptions

Japanese text is allowed only in:

- the `*.ja.md` files listed above
- `.agents/skills/natural-japanese/` (third-party skill, managed by `skills-lock.json`, not edited by hand)
- this policy and the rule file, where Japanese appears as an example (the language switch line, the terminology table)
- test data that must be non-ASCII on purpose (for example a file name that checks Unicode handling). Write the reason in a comment next to it

To find Japanese text outside these places:

```bash
git ls-files | grep -v -E '\.ja\.md$|^\.agents/skills/natural-japanese/' \
  | LC_ALL=C.UTF-8 xargs grep -l -P '[\x{3040}-\x{30FF}\x{4E00}-\x{9FFF}]'
```

The result must list only this policy, the rule file, the writing skill, and allowed test data.

## History

Before issue #290 the repository was written in Japanese. Git history, past Issues, and past PRs are not rewritten.
