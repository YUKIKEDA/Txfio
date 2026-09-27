# Conventions

English | [日本語](conventions.ja.md)

Repository conventions, so that the same points are not repeated in code review. This is the detailed human-readable source. The summary is [`CONTRIBUTING.md`](../CONTRIBUTING.md), and enforcement is `.cursor/rules/`.

## Repository layout

- The library is in `src/Txfio/`, unit tests in `tests/Txfio.Tests/`, stress tests in `tests/Txfio.Stress/`, and the temporary directory and `WindowsFact` that both use in `tests/Txfio.TestSupport/`
- Each `.csproj` is at the root of its project. Do not put projects directly at the repository root
- `.cs` files go in feature folders. Directly under a project there are only the entry point (static `Txfio`), the root of the implementation, and the `.csproj`. A new feature gets a new folder; do not scatter files directly under the project
- Tests create the same `<Area>/` under `tests/Txfio.Tests/` as `src/Txfio/<Area>/`. File names go `Foo.cs` → `FooTests.cs`
- Namespaces do not follow folders. The library is `Txfio`, unit tests and stress tests are `Txfio.Tests`, and shared helpers are `Txfio.Tests.Support`
- **Commit `Txfio.slnx`**. Do not add a `.sln`

The public surface is only the contracts (`Txfio`, `ITransaction`, result types, exceptions, progress). Implementation types are `internal`. The test assemblies get `InternalsVisibleTo`.

## Feature folders

Do not add feature files directly under the project. If there is no folder to add to, create one. For example:

```text
src/Txfio/
  Txfio.cs                 entry point (BeginAsync / Recover)
  Contracts/               public contracts (interfaces, exceptions, result types)
  Journal/
  Staging/
  Commit/
  Recover/

tests/Txfio.TestSupport/
  TempDirectory.cs         temporary directory. No Facts here
  WindowsFactAttribute.cs
tests/Txfio.Tests/
  TxfioTests.cs
  Support/                 unit test fixtures. No Facts here
  Contracts/
  Journal/
tests/Txfio.Stress/
  stress tests. The gate's dotnet test does not run them
```

## Partial classes

The default is **one type per file**. A long file alone is not a reason for partial. Move helpers into another type.

Only the transaction implementation type may use partial files that match feature folders.

- Put **only one** `Type.<Area>.cs` in a feature folder. Do not add a second one to the same folder
- Test classes are not partial

## Using aliases

- Do not use type aliases such as `using IoFile = System.IO.File;` to shorten code
- When a name collides with the BCL `File` / `Directory`, fully qualify it as `System.IO.File` / `System.IO.Directory`

## Async and ownership

- `await` in the library uses `ConfigureAwait(false)`
- `CancellationToken` is the last parameter
- A `Stream` a method receives is **not disposed by the callee** (the caller owns it). Close only handles you opened yourself

## Paths

Internally, compare normalized absolute paths, and decide the lock order by them. Path comparison uses `OrdinalIgnoreCase`.

## Language

The repository language is English. Which files are bilingual, and how translations stay in sync, is in [`docs/language.md`](language.md).

## XML comments on the public API

- Give `public` types and members XML documentation comments (`<summary>` is required; add parameters, return values, and exceptions when a reader might be unsure)
- **Also give them to `public` members of internal types** (for example the `ITransaction` members of the implementation type, and public constructors for JSON)
- `internal` types and members get them too (StyleCop `documentInternalElements`). Tests and `private` members are not required
- When an implementation has the same contract as the interface, use `/// <inheritdoc />`. Do not write it again
- The library project has `GenerateDocumentationFile=true`. Missing docs are errors (CS1591)
- Test projects do not generate XML documentation (no CS1591)
- XML documentation and ordinary comments are in **English**
- Write full sentences. Every XML documentation element ends with a period (StyleCop SA1629). Inline comments are sentences too; a short phrase without a period is fine for a one-line inline comment
- Follow the StyleCop wording for summaries: properties start with "Gets" / "Gets or sets" (SA1623), and constructors start with "Initializes a new instance of the <see cref="T"/> class" (SA1642)
- Use the terms in [`.cursor/skills/english-writing/SKILL.md`](../.cursor/skills/english-writing/SKILL.md). For example, the design term "sentinel" is written "work-folder lock" everywhere. When a review finds wording to correct, add the general rule to that skill in the same change

## How design docs and XML docs share the work

The source of truth for behavior is `docs/design.md`. Writing the same rule in several places makes the wording drift and causes missed updates.

### What XML docs (on the public surface such as `ITransaction`) contain

- Only the contract a caller needs before and right after the call
  - What it does
  - The meaning of the parameters
  - The exception types it throws, with a short condition
  - The return value
- Do not write implementation or design details such as how locks are taken, how the journal is written, folding, or the order of cleanup. If needed, refer to it: "see the design, section X"
- When they disagree with the design, the design wins. Fix the XML docs to match the design

### Shape of an API section in `docs/design.md`

Each API has its own section, with these headings in this order. A heading that does not apply says "None"; do not omit it (so that it can be told apart from a missing section).

| Heading | Contents |
| --- | --- |
| Overview | What it schedules. The operation kind (`PendingChangeKind`) |
| Preconditions | When it can be called, conditions on paths, combinations with existing scheduled changes (folding and rejection) |
| Locks | The locks it takes and how it waits. Cross-cutting rules are in "Concurrency and locks"; write only what is specific to this API |
| Journal | The lines it writes, and when. The cross-cutting order is in "Journal and recovery" |
| Exceptions | A table of exception types and conditions |
| Cleanup on failure | What is deleted and what stays when it fails partway |

- Rules that span several APIs, such as the lock order, the order of journal writes, and the commit steps, are written once in their cross-cutting section. API sections refer to them instead of repeating them
- When adding a rule, first decide whether it belongs to a cross-cutting section or an API section, and write it in one place only

## Tests

- Test method names are English: `{Target}_{Behavior}` in PascalCase, where `{Target}` is the method or type under test and `{Behavior}` states the expected behavior, for example `AddAsync_ShortAndLongNamesShareOneLock`. Keep them readable; do not abbreviate
- Every `[Fact]` / `[Theory]` has an XML comment with **Given**, **When**, and **Then** in `<remarks>` (`<para>Given: ...</para>` and so on). Helpers do not need them
- `tests/Txfio.TestSupport/` holds the temporary directory and `WindowsFact`. No Facts there
- Unit test fixtures go in `tests/Txfio.Tests/Support/`. No Facts there
- Each test creates its own unique temporary directory and deletes it on dispose. Assume parallel execution
- Stress tests that check promises with random input go in `tests/Txfio.Stress/`. There is no matching `src/` folder
  - Random operation sequences are compared with an in-memory model, and on failure the sequence is shrunk by removing steps. There are sequences for file Add / Update / Delete / Move / Read; directory create, delete of an empty directory, delete of a tree, Move without overwrite, and Move that replaces the destination; Copy / Import / Export of files and directories; text and JSON reads and writes; and ZIP create, extract, import, and export. Directory contention has two versions: a few directories competing without waiting, and more directories retrying on contention. Crashes: the parent kills the child, and after Recover the disk is compared with the record or with one step after it
  - Multi-process stress tests start the stress project's own executable as the child process (the entry in `Program.cs`)
  - The `dotnet test` of the gate (`./build.ps1` and the Linux pre-PR check) runs only `tests/Txfio.Tests/Txfio.Tests.csproj`. Run stress tests explicitly with `dotnet test tests/Txfio.Stress/Txfio.Stress.csproj`. By default they are sized to finish quickly in that run. To run longer or larger, change the environment variables `TXFIO_STRESS_SEED`, `TXFIO_STRESS_ITERATIONS`, `TXFIO_STRESS_PROCESSES`, `TXFIO_STRESS_FILES`, `TXFIO_STRESS_MAX_BYTES`. Passing the seed from a failure message reproduces the same sequence
  - Multi-process stress has two versions: a few files competing without waiting (more combinations of contention) and more files retrying on contention (load closer to real use). The result counts are printed in the test output
  - Do not hide bugs found by these tests; open a separate Issue

### Windows-only tests and running on Linux

Only Windows is guaranteed at run time (`docs/design.md`, "Target platform"). Still, as a development environment, the unit tests can run with `dotnet test` on Linux too.

- Tests that rely on Windows behavior itself use `[WindowsFact("reason")]` (`tests/Txfio.TestSupport/`). This covers junctions, drive-letter paths, and open files that cannot be renamed or deleted. On other operating systems they are skipped as `Windows only: reason`
- Use `/` or `Path.Combine` for path separators in tests too. `\` is part of a file name on Linux
- On Linux, run `dotnet test tests/Txfio.Tests/Txfio.Tests.csproj` before opening a PR; every test except `WindowsFact` must pass. Stress tests are not part of this check. The merge gate stays `./build.ps1` on Windows, where no tests are skipped
- Lock contention detection (`PathLockSet.IsSharingViolation`) also treats Linux EAGAIN (`HResult` = 11) as a sharing violation. This is only so the tests run; it is not a run-time guarantee. macOS is not added until checked on a real machine

## Formatting

- The source of truth is `.editorconfig` at the repository root
- File-scoped namespaces, ImplicitUsings
- Write variable types explicitly. Do not use `var`
- Write collections and arrays as `new List<T>()` and `new T[] { }`. Do not use collection expressions `[]`, target-typed `new()`, conversions to object initializers, or primary constructors
- Do not replace `Substring` with range operators or indexes from the end. Do not simplify lambdas to method groups
- Namespaces do not follow folders ("Repository layout" above). Do not use folder namespaces such as `Txfio.Archive`
- In private methods whose last parameter is a callback, put `CancellationToken` before the callback
- A public method's parameter name may be passed from a helper as the parameter name of an `ArgumentException`
- Lock restoration in `finally` catches only contention and cancellation; other exceptions reach the caller
- Do not change code for performance suggestions such as changing to concrete types, making instance methods static, or moving constant argument arrays to static fields
- Line endings are **LF** (fixed by `.gitattributes`)
- Run `dotnet format` before submitting. CI / `build.ps1` check with `--verify-no-changes`
- Compiler warnings are errors (`TreatWarningsAsErrors`)
- Naming follows **.NET conventions**
  - Private fields (including static) are `_camelCase`
  - Constants are PascalCase
  - Do not prefix instance members with `this.` (only when needed to avoid a name collision)
  - StyleCop SA1101 / SA1306 / SA1309 / SA1310 / SA1311 are disabled (they conflict with the above)
- Interface names must have the `I` prefix (SA1302 stays enabled)
- No logging framework in the library. Diagnostics are return values and exceptions only
