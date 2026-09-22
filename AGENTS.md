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

If `dotnet --list-sdks` does not show **10.x**, install a .NET 10 SDK yourself. Do not stop the session to ask the user. TFM is `net8.0`.

| Host | Verify |
| --- | --- |
| Windows | `./build.ps1` (full gate: restore → format verify → build → test) |
| Linux | `dotnet restore Txfio.slnx` then `dotnet format Txfio.slnx --verify-no-changes` then `dotnet build Txfio.slnx`. **Do not run `./build.ps1`.** Do not treat `dotnet test` as Done |

## Always-apply rules

- [`.cursor/rules/workflow.mdc`](.cursor/rules/workflow.mdc)
- [`.cursor/rules/conventional-commits.mdc`](.cursor/rules/conventional-commits.mdc)
- [`.cursor/rules/pull-requests.mdc`](.cursor/rules/pull-requests.mdc)
- [`.cursor/rules/engineering.mdc`](.cursor/rules/engineering.mdc)
- [`.cursor/rules/japanese-docs.mdc`](.cursor/rules/japanese-docs.mdc)

## Working agreements (summary)

- Do not start coding without a GitHub Issue and branch. Roadmap text is not a start signal
- Grill (`.cursor/skills/grilling`) when design still branches; skip when the Issue already has acceptance criteria and matches `docs/design.md`
- Design-changing work needs a **design PR first** (typos/examples may ship with code)
- Branch: `type/<issue-number>-<slug>`
- Commits / PR titles: Conventional Commits (Japanese subject OK)
- Local verification gate: `./build.ps1` against **`Txfio.slnx`** on Windows
- Layout: `src/Txfio` ↔ `tests/Txfio.Tests`, TFM `net8.0`, namespace `Txfio`
- Comments (XML docs / inline) in **Japanese**; do not end them with `。` or `.`
- New or changed `src/` XML docs: Gemini reviews natural Japanese before the PR is ready (`.cursor/rules/japanese-docs.mdc`)
- Test methods use natural Japanese names plus 前提 / 手順 / 期待 in remarks

## Current backlog pointer

See [`docs/roadmap.md`](docs/roadmap.md). Next coding work: grill if needed, then GitHub Issue, then branch, then PR for human review.
