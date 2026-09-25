# Txfio — agent entry point

Start here when working in this repository as an agent.

Human-readable source of truth: [`CONTRIBUTING.md`](CONTRIBUTING.md). Coding details: [`docs/conventions.md`](docs/conventions.md).

## Canonical docs (do not use `.dev/` for lasting decisions)

| Doc | Path | Role |
| --- | --- | --- |
| Design | [`docs/design.md`](docs/design.md) | Product/architecture contracts |
| Roadmap | [`docs/roadmap.md`](docs/roadmap.md) | Phases (provisional) |
| Conventions | [`docs/conventions.md`](docs/conventions.md) | Layout, ownership, tests |
| Contributing | [`CONTRIBUTING.md`](CONTRIBUTING.md) | Human-readable process |
| Cursor rules | [`.cursor/rules/`](.cursor/rules/) | Always-applied enforcement |

`.dev/` is **temporary scratch**. Do not leave permanent decisions only there.

## Environment

If `dotnet --list-sdks` does not show **10.x**, install a .NET 10 SDK yourself. Do not stop the session to ask the user. TFM is `net8.0`, so running tests also needs the **.NET 8 runtime** (`dotnet --list-runtimes` shows `Microsoft.NETCore.App 8.x`).

| Host | Verify |
| --- | --- |
| Windows | `./build.ps1` (full gate: restore → format verify → build → test) |
| Linux | `dotnet restore Txfio.slnx` then `dotnet format Txfio.slnx --verify-no-changes` then `dotnet build Txfio.slnx` then `dotnet test Txfio.slnx`. **Do not run `./build.ps1`.** Before opening a PR, every test must pass except the ones skipped as `Windows 専用` (`WindowsFact`). This is a pre-PR check, not the merge gate: the merge gate stays `./build.ps1` on Windows |

## Always-apply rules

- [`.cursor/rules/workflow.mdc`](.cursor/rules/workflow.mdc)
- [`.cursor/rules/conventional-commits.mdc`](.cursor/rules/conventional-commits.mdc)
- [`.cursor/rules/pull-requests.mdc`](.cursor/rules/pull-requests.mdc)
- [`.cursor/rules/engineering.mdc`](.cursor/rules/engineering.mdc)
- [`.cursor/rules/design-docs.mdc`](.cursor/rules/design-docs.mdc)
- [`.cursor/rules/japanese-docs.mdc`](.cursor/rules/japanese-docs.mdc)
- [`.cursor/rules/similar-findings.mdc`](.cursor/rules/similar-findings.mdc)

## Working agreements (summary)

- Do not start coding without a GitHub Issue and branch. Roadmap text is not a start signal
- Grill (`.cursor/skills/grilling`) when design still branches; skip when the Issue already has acceptance criteria and matches `docs/design.md`
- Design-changing work needs a **design PR first** (typos/examples may ship with code)
- Branch: `type/<issue-number>-<slug>`
- Commits / PR titles: Conventional Commits (Japanese subject OK)
- Local verification gate: `./build.ps1` against **`Txfio.slnx`** on Windows
- Layout: `src/Txfio` ↔ `tests/Txfio.Tests`, TFM `net8.0`, namespace `Txfio`
- Private fields: `_camelCase`. Do not prefix members with `this.` unless needed for disambiguation
- Comments (XML docs / inline) in **Japanese**; no `。` or `.` mid-sentence or at the end. Wording that Gemini or a human already corrected lives in [`.cursor/skills/japanese-writing/SKILL.md`](.cursor/skills/japanese-writing/SKILL.md). Read it before writing comments, and add a new general rule there in the same change when a review finds one
- New or changed `src/` XML docs: Gemini reviews natural Japanese before the PR is ready (`.cursor/rules/japanese-docs.mdc`)
- Test methods use natural Japanese names plus 前提 / 手順 / 期待 in remarks
- When a problem is pointed out, search for the same kind of gap before fixing only the cited spot (`.cursor/rules/similar-findings.mdc`)

## Current backlog pointer

See [`docs/roadmap.md`](docs/roadmap.md). Next coding work: grill if needed, then GitHub Issue, then branch, then PR for human review.
