# Contributing to Txfio

English | [日本語](CONTRIBUTING.ja.md)

This document is the **human-readable source** of the development rules. The agent entry point is [`AGENTS.md`](AGENTS.md), and the enforced rules are in `.cursor/rules/`. Coding conventions are in [`docs/conventions.md`](docs/conventions.md). The source of truth for the design is [`docs/design.md`](docs/design.md), and the implementation order is [`docs/roadmap.md`](docs/roadmap.md). Which language to write in is [`docs/language.md`](docs/language.md).

`.dev/` is for drafts only. Do not leave decisions in `.dev/`.

## Language

The repository language is **English**: code, comments, tests, docs, commit messages, Issues, and PRs. `README`, `CONTRIBUTING`, and the main docs in `docs/` also have a Japanese translation (`*.ja.md`). When you change one of them, change its translation in the same PR. The details are in [`docs/language.md`](docs/language.md).

## Required workflow

Work that touches code or conventions **always** goes in this order. Starting implementation without an Issue, a branch, or a PR is not allowed.

```text
(grill if design decisions remain) → create Issue → (grill again / checklist if not implementable) → create branch → work → PR → human review → squash merge → repeat
```

1. **Grill** (before creating the Issue): when decisions branch, such as API, semantics, phase boundaries, or exception policy. The skill is [`.cursor/skills/grilling/SKILL.md`](.cursor/skills/grilling/SKILL.md). It may be skipped when the Issue has acceptance criteria and non-goals and does not conflict with `docs/design.md`
2. **Create the Issue** (a template is required; blank Issues are not allowed)
3. **Grill again** only when the work cannot be broken down to an implementable size, and write the answers back into the body or a checklist
4. **Create the branch:** `type/<issue-number>-<slug>` (the Issue number is required)
5. **Work** (`./build.ps1` on Windows)
6. **Open the PR** (follow the template strictly; `Closes #N` on its own line in `## Related`)
7. **Human review → squash merge**
8. Next Issue

- Agents must not skip the Issue / branch / PR and start writing the implementation
- Agents do not merge PRs that touch the design on their own
- Planned items in the roadmap are not a signal to start work until they become GitHub Issues

### Foundation batch exception

Only the first repository foundation (Issue #1) may put conventions, templates, and the empty library into one Issue = one PR. From the second one on, the normal granularity applies.

## External contributors

Grilling is a maintainer's tool. External contributors follow these rules.

- **Open an Issue first, then a PR.** Walk-in PRs (without an Issue) are not accepted
- Discuss proposals that touch the design in an Issue. The maintainer decides on merging, and grills if needed
- Only maintainers can push directly (fork + PR)

## Issues and PRs

- **1 Issue ≈ 1 PR**
- Issue types: **feat** / **bug** / **task** / **design** / **spike**
- Branch names: `type/<issue-number>-<slug>` (for example `feat/12-commit-rename`)
- Commit / PR titles: [Conventional Commits](.cursor/rules/conventional-commits.mdc), in English
- Recommended scopes: `txfio`, `test`, `build`, `ci`, `docs`
- The PR body uses the headings of [`.github/pull_request_template.md`](.github/pull_request_template.md) exactly
- **Linking the Issue (required):** in `## Related` of the PR body, write a closing keyword that GitHub recognizes, **on its own line** (`Closes #12`). A bullet or a bare URL may fail to link

### Labels

- `type:feat` / `type:bug` / `type:task` / `type:design` / `type:spike`
- `phase:0` … `phase:3`

## Design change process

- A change that touches **contracts, public API, semantics, phase boundaries, or exception policy** first merges a **Design Issue + design PR** that updates `docs/design.md`, then the implementation Issue proceeds
- **Typos, clearer wording, or examples only** may include the design diff in the implementation PR
- "Implement first and update the design later" is not allowed
- If the design needs to change during implementation, stop implementing and open the design PR first
- The written design is the decision made at that time. When proposing, do not use its sentences as the reason to keep the current shape. Propose the best solution now, and if it differs, change the design. Do not add public API only to keep a shape

## Repository layout

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

- Solution format: **`.slnx` only** (do not use or keep a `.sln`)
- Target: **`net8.0`** (the run-time guarantee is Windows)
- Tests: **xUnit**
- Coding conventions in detail: [`docs/conventions.md`](docs/conventions.md)

## Versions

SemVer. `0.x` may have breaking changes. The source of truth for the version is the git tag (`v0.1.0` and so on). Pushing to nuget.org happens after the GitHub repository is public (after Phase 3 is done).

## Local verification (source of truth)

The GitHub Actions workflow is in the repository, but **CI may not run because of usage limits**. The gate before merge is `./build.ps1` on Windows. Once Actions run, a green CI will be required too (the PR template will get a checkbox then).

```powershell
./build.ps1
```

It runs `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test`. Restore / format / build target **`Txfio.slnx`**. `dotnet test` targets **`tests/Txfio.Tests/Txfio.Tests.csproj`**. Stress tests (`tests/Txfio.Stress`) are not part of the gate; run them explicitly with `dotnet test tests/Txfio.Stress/Txfio.Stress.csproj`.

This script is **Windows only**. On Linux, do not run `./build.ps1`; run restore / format / build on `Txfio.slnx` in order, then run `dotnet test tests/Txfio.Tests/Txfio.Tests.csproj`. Before opening a PR, every unit test must pass except those skipped as `Windows only` (`WindowsFact`). This is a condition before the PR, not the merge gate. The test record before merge must come from Windows. Running the tests needs the .NET 8 runtime in addition to the .NET 10 SDK.

Crash injection and SMB verification are required locally only when the Issue's acceptance criteria say so.

In the PR's Verification, state that `./build.ps1` was run.

## Merging

- **Squash merge only**
- A human decides whether to merge PRs written by agents
