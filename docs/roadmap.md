# Roadmap

English | [日本語](roadmap.ja.md)

The phases and their order are **provisional**. The implementation order and boundaries may change through grilling and design PRs.

The source of truth for semantics, API, and non-functional requirements is [`design.md`](design.md). This file is only a map of the implementation order.

Check an item only when it is **merged into main**. While a PR is open, write only the Issue number.

## Where we are

| Span | State |
| --- | --- |
| Phase 0 repository foundation | Done |
| Phase 1 MVP | Done |
| Phase 2 concurrency and locks | Done |
| Phase 3 extensions and publishing | In progress (next: nuget.org) |

The Phase 3 implementation (Issues #33, #35, #37, #39, #45, #47, #49, #51, #57, #67, #73, #79), the user README (Issues #41, #54, #69, #75), and the design (Issues #43, #53, #65, #71, #77, #109, #117) are in main. Creating from a list (Issue #119) is in main. Limiting the exclusive work-folder lock to the duration of a call (Issue #164; design in Issues #95 and #97) is in main. Detecting external changes after staging (Issue #168; design in Issue #96) is in main. Making text and JSON methods of `ITransaction` (Issue #98) is in main. Rejecting reparse points inside the work folder instead of following them (Issue #106; design in Issue #172) is in main. Not turning moves at commit and recovery into copy and delete (Issue #105) is in main. Normalizing lock names to long names (Issue #107) is in main. Dispose cleanup no longer hiding the original exception (Issue #104) is in main. Recovering orphaned journals in path order (Issue #108) is in main. Grouping per-kind processing into one type per operation kind (Issue #99) is in main. Deriving restage folding from a path table (Issue #100) is in main. Merging directory import into one place (Issue #171) is in main. The design that makes the exclusive check one marker (Issue #182) is in main. Its implementation (Issue #102) is in main. Making the `.prev` restage backup a rename (Issue #103) is in main. Moving test hooks to an injection point (Issue #101) is in main. GitHub is public (Issue #59). Next: making English the repository language (Issue #290), then publishing to nuget.org.

## Phase 0 — Repository foundation

- [x] Issue #1: conventions, templates, empty library, `./build.ps1`

## Phase 1 — MVP (provisional)

One process, one transaction. Cooperative locks are skipped. `tx.ReadAsync` is outside this phase.

### Lifetime and writes

- [x] Issue #3: `BeginAsync` / empty `CommitAsync` / uncommitted Dispose / `RecoverAsync` (no operations)
- [x] Issue #5: `AddAsync` / `UpdateAsync`, `.txnew`, `File.Move` at commit, sidecars in Recover
- [x] Issue #7: `DeleteAsync` (schedule only, delete at commit. Directory delete not included)
- [x] Issue #9: file `MoveAsync` (same volume only. Directory Move is Phase 3)
- [x] Issue #11: `AttachAsync` (register in the journal without touching the file. Record size and last write time as the expected state)
- [x] Issue #13: directory delete semantics (fix `DeleteAsync` to direct children only, with nothing pulled in implicitly)
- [x] Issue #15: directory `DeleteAsync` (direct children only, non-recursive delete at commit)

### Finishing the journal, commit, and Recover

- [x] Issue #17: remaining normalization of the same path and dependencies (Update / Delete at a Move destination, Delete at a Move source. Update / Delete / Move after Attach are #11)
- [x] Issue #19: fixed apply order (Add / Move / Attach → Update → Delete. Directory Delete from the deepest path)
- [x] Issue #21: Before / After (files: size and last write time; directories: existence only. Recover decides "applied" with them)
- [x] Issue #23: custom exceptions (`ExternalConflictException` and `UnsupportedOperationException`. Lock contention is Phase 2)
- [x] Issue #25: crash injection (`AfterCommitting` and `AfterApply`. Dispose does not roll back, and Recover verifies the real files)

## Phase 2 — Concurrency (provisional)

- [x] Issue #27: path locks at operation time (`.txfio/locks/`, Move in lexical order, two transactions in one process. Files are not deleted)
- [x] Issue #29: Recover neither opens nor deletes `.lock` (the files remain after a crash, and another transaction can take them again)
- [x] Issue #31: concurrent access from other processes (contention while held, different paths, retaking after exit without Dispose. SMB not included)

## Phase 3 — Extensions (provisional)

- [x] Issue #33: `ReadAsync` (`.txnew` if present, otherwise the real file. No locks)
- [x] Issue #35: copy progress for Add / Update (`TransferProgress`. No reports for backups and the journal. Commit count not included)
- [x] Issue #37: Import / Export (copy in from outside + Add, read copy out. The source is not deleted)
- [x] Issue #39: directory Move (one rename. The work-folder lock is exclusive while it runs)
- [x] Issue #41: user README (what it can and cannot do, comparison with TxFileManager and SQLite)
- [x] Issue #43: design: delete all, directory Attach, Copy, directory Import / Export (no implementation)
- [x] Issue #45: `DeleteTreeAsync` (schedule deleting everything under a directory. Staging does not walk the tree; recursive delete at commit. The work-folder lock is exclusive while it runs)
- [x] Issue #47: directory Attach (existence only. Changes in children are not checked. Not deleted by rollback)
- [x] Issue #49: `CopyAsync` (a file or directory inside the work folder. The source stays. A directory is an Add per file)
- [x] Issue #51: directory Import / Export (Add an external directory, and send an internal directory out without locking)
- [x] Issue #53: design: text and JSON extension methods (no implementation)
- [x] Issue #54: turn how to write the README into a skill, and rewrite the README
- [x] Issue #57: text and JSON extension methods
- [x] Issue #65: design: `CreateDirectory` (create an empty directory right away; discard deletes that tree. No implementation)
- [x] Issue #67: `CreateDirectoryAsync` (create an empty directory right away; discard deletes that tree)
- [x] Issue #69: describe the branches of each operation in the README
- [x] Issue #71: design: allow normal operations under `CreateDirectory` (no implementation)
- [x] Issue #73: allow normal operations under `CreateDirectory`
- [x] Issue #75: draw the conditional branches of each operation in the README as diagrams
- [x] Issue #77: design: remove `AttachAsync` (no implementation)
- [x] Issue #79: remove `AttachAsync`
- [x] Issue #109: design: creating and extracting ZIP archives (four methods: Create / Export / Extract / Import. No implementation)
- [x] Issue #111: creating ZIP archives (`CreateArchiveAsync` / `ExportArchiveAsync`)
- [x] Issue #113: extracting ZIP archives (`ExtractArchiveAsync` / `ImportArchiveAsync`. All entry names are checked before writing)
- [x] Issue #115: describe ZIP archive operations in the README
- [x] Issue #117: design: specify files and names as pairs when creating a ZIP (no implementation)
- [x] Issue #119: specify files and names as pairs when creating a ZIP
- [x] Issue #121: design: Recover skips journals of live transactions (no implementation)
- [x] Issue #81: a liveness lock per transaction, `.txfio/tx-{guid}.lock` (Recover processes only journals whose lock it can open)
- [x] Issue #82: reject `BeginAsync` and `CommitAsync` while an orphaned journal exists (`RecoveryRequiredException`). Recover holds the work-folder lock exclusively and deletes conflicting journals too
- [x] Issue #83: an exception after `Committing` is written does not make Dispose roll back. The exception is rethrown and the next `RecoverAsync` rolls forward
- [x] Issue #129: design: make journal overwrites atomic, and keep unreadable journals as `JournalUnreadable` (no implementation)
- [x] Issue #84: make journal overwrites atomic, and keep unreadable journals
- [x] Issue #132: design: write directories a copy creates to the journal before creating them (no implementation)
- [x] Issue #85: do not create `.txnew` and copy destination directories before the journal
- [x] Issue #86: apply an Update whose `.txnew` remains even when Before and After are the same
- [x] Issue #136: design: a Move that differs only in case throws InvalidOperationException (no implementation)
- [x] Issue #87: a Move that differs only in case throws InvalidOperationException
- [x] Issue #139: design: overlapping calls on the same transaction throw InvalidOperationException (no implementation)
- [x] Issue #88: overlapping calls on the same transaction throw InvalidOperationException
- [x] Issue #142: design: text and JSON writes to a Move destination are Updates (no implementation)
- [x] Issue #90: text and JSON writes to a Move destination are Updates
- [x] Issue #145: design: Move chains are applied from the free end (no implementation)
- [x] Issue #91: Move chains are applied from the free end
- [x] Issue #92: design: the view inside a transaction is the post-commit view (no implementation)
- [x] Issue #148: align Read, Exists, and text writes with the view inside a transaction
- [x] Issue #93: design: give commit and recovery results paths and reasons (no implementation)
- [x] Issue #157: give commit and recovery results the failed paths and reasons
- [x] Issue #94: design: allow waiting for locks with a timeout (no implementation)
- [x] Issue #161: allow waiting for locks with a timeout
- [x] Issue #95: design: do not hold the exclusive work-folder lock until the transaction ends (no implementation)
- [x] Issue #97: design: detect lock conflicts between ancestor and descendant paths at staging (no implementation)
- [x] Issue #164: limit the exclusive work-folder lock to the call, and schedule what is under a directory with intent locks
- [x] Issue #96: design: an option to detect external changes after staging (lost update) (no implementation)
- [x] Issue #168: detect external changes after staging

## Left before publishing

- [x] Issue #98: make text and JSON methods of `ITransaction`
- [x] Issue #99: group per-kind processing into one type per operation kind
- [x] Issue #100: derive restage folding from the table of paths as the transaction sees them
- [x] Issue #101: move test hooks from a static AsyncLocal to an injection point
- [x] Issue #102: taking the exclusive work-folder lock opens every earlier .lock, so it gets slower over time
- [x] Issue #103: make the `.prev` restage backup a rename instead of a full copy
- [x] Issue #104: an exception from DisposeAsync cleanup hides the original exception
- [x] Issue #105: the volume check always passes, and a Move across a mount point becomes an implicit copy
- [x] Issue #106: writes can escape the work folder through a junction inside it
- [x] Issue #107: lock file name normalization does not match NTFS case rules or short names
- [x] Issue #108: the order in which Recover processes several orphaned journals is not defined
- [x] Issue #171: merge directory import into one plan-and-apply path
- [ ] Issue #290: make English the repository language, with Japanese translations of frequently read documents

## Publishing (after Phase 3)

- [x] Issue #59: make the GitHub repository public
- [ ] Publish to nuget.org (until then, `dotnet pack` and metadata only)
