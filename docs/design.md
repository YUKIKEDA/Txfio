# Txfio design

English | [日本語](design.ja.md)

2026-09-26

For the order of implementation, see [`roadmap.md`](roadmap.md). Phase boundaries are provisional and may change.

## Overview

A transactional file IO library for C#, inspired by git staging and commit, that works even where IO is slow, such as on a file server.

The existing `TxFileManager` was rejected because its journal is volatile.

### Non-functional requirements

- Work smoothly where IO is slow, such as on a file server. Avoid needless copies and needless IO, and keep IO cost to a minimum
- Do not use a temporary folder (a separate location outside the work folder)
- Assume desktop use, and accept "dirty reads" that ordinary file IO can see (full isolation across processes is not a goal)

### Target platform

Start as Windows only (NTFS and SMB file servers), and leave room for cross-platform support later.
The package has a single TFM, `net8.0` (not `net8.0-windows`).
Only Windows is guaranteed at run time.

## Transaction semantics

- **Crash recovery is guaranteed**: even if the process is killed, the next start detects the uncommitted transaction and recovers it by rolling back or rolling forward
- **Full rollback**: on abort, Txfio discards the changes it staged itself (`.txnew` files, directories that `CreateDirectory` created, journal entries).
  Whatever was written with the plain file API inside a `CreateDirectory` directory is deleted together with that directory.
  Other files that this transaction did not touch are not restored to their state before the transaction; they stay as they are now, after any external change
- **No full atomicity across files**: a commit is itself a series of renames and deletes over several files, so while a commit runs, other processes can see an intermediate state where some files are new and the rest are old.
  This is accepted as part of the first premise (dirty reads are allowed)
- OS-level transactions (Transactional NTFS / TxF) are not used.
  Microsoft has deprecated TxF and says it may be removed from future Windows. TxF is also essentially an isolation mechanism, which does not fit the "dirty reads are allowed" policy

## Write model (the core design decision)

### Apply at commit

While a transaction runs, it does not touch the real paths as a rule.
Changes are staged in a `.txnew` sidecar in the same directory as the target file.
The exception is `CreateDirectoryAsync`: only an empty directory is created at the real path when the method is called.
Its contents are written with the plain file API.

The writer may be the same process or another process.
Under that directory, the same Txfio operations work as under a directory whose parent existed from the start.
Discarding the transaction deletes the directory with its contents.

- Update: first write the new content to `.txnew` and fsync it.
  Only at commit, replace the original path with `File.Replace(txnew, target, destinationBackupFileName: null, ignoreMetadataErrors: true)` (Win32 `ReplaceFileW`).
  Methods such as "delete, then move", where the target path briefly does not exist, are not used.
  `ReplaceFileW` moves the ACL, attributes, creation time, short name, and alternate data streams of the replaced file to the new file.
  A rename with `File.Move(overwrite: true)` would change these to those of `.txnew` (the ACL inherited from the parent folder, and the commit time as the creation time), so it is not used.
  The contents and last write time come from `.txnew`, so the After check (size and last write time) does not change.
  When the metadata cannot be moved (for example the SMB server does not support it), the replacement of the contents wins and the operation does not fail (`ignoreMetadataErrors`).
  Add and Move stay renames
- Delete: the target (a file or a directory) is untouched until commit.
  Only the scheduled delete is recorded in the journal; the actual delete happens at commit.
  A directory is deleted with non-recursive `Directory.Delete` (after its children are gone)
- Add: create a new `.txnew`, and rename it to the real path at commit
- Move/Rename: only record "old path → new path" in the journal, and rename at commit

### Why this model

The first idea was to apply immediately: "before Update, move the original aside with a rename (`.txbak`), then write the new contents directly to the original path". That brings complexity: (1) backups need generations when a file is changed several times, and (2) rollback needs a round trip to put the backups back.
Applying at commit makes rollback just "discard `.txnew` and the journal entries", and the whole `.txbak` backup and restore mechanism is no longer needed.
The IO cost is the same (the data is written at the same time; only the moment it reaches the real path changes), but the mental model is much simpler.

### Trade-offs

- A commit becomes "run the renames and deletes for all changed files together", so the time a commit takes grows with the number of changed files.
  Progress by count is not implemented yet; copy progress is the `TransferProgress` of Add / Update
- The application cannot read back its own uncommitted changes with plain file IO (`File.ReadAllText` and so on).
  To read them back, use the dedicated API `tx.ReadAsync(path)` (the "post-commit view").
  To check existence, use `tx.ExistsAsync(path)`

### Atomicity of Update

Even if the process crashes before the write to `.txnew` completes, the original path always holds only "the complete state before the change" (applying at commit means the real path is never touched before commit, so the choice of write model solves this problem automatically).

### Naming of `.txnew`

The file name includes the transaction ID as well as the target file name (for example `foo.txt.{txid}.txnew`).
This lets Recover tell exactly which transaction a `.txnew` left after a crash belongs to.
The restage backup is `foo.txt.{txid}.txnew.prev`.
Rollback also deletes this backup ("Created directories").

### Created directories

#### Not an operation kind

Directories that `CopyAsync`, directory `ImportAsync`, `ExtractArchiveAsync`, and `ImportArchiveAsync` create are not an operation kind.

- They do not appear in `GetPendingChanges`
- They are written to `createdDirectories` in the journal document, as an array of paths relative to the work folder ("Journal paths")
- A journal without this field, or with null, is read as empty
- The document version stays 1
- Directories that `ExportAsync` creates outside the work folder are not in this list, and `RecoverAsync` does not delete them

#### Write the journal before creating anything

Directory copy, Import, and extract gather the directories to create and the Add for each file in memory, then write the journal once.
After that they create the directories and the `.txnew` files.
Even when there are no files and only empty directories, the list is written before the directories are created.

`AddAsync`, `UpdateAsync`, and file `CopyAsync` write the operation to the journal before writing `.txnew`.

`.prev` is used only to put the original `.txnew` back when the call fails partway.

#### On failure, remove the added plan and rewrite

When creating something or writing a `.txnew` fails, remove the operations and created directories that the call added, delete what was created, then write the journal again.
If that write fails too, the plan is still in the journal, so the next `RecoverAsync` deletes them.

#### Delete only when there is no `Committing`

Rollback deletes the `.txnew` files named in the operations and their backups with `.prev` appended, then deletes `createdDirectories` deepest first, without recursion.
A backup is created only for the `.txnew` of the same operation that is restaged, so the work folder is not scanned (discard and Recover do not stop even if some folder cannot be read).

When there is `Committing`, created directories are not deleted.

#### Dispose does not throw when something is left

Dispose keeps deleting to the end without throwing `IOException` or `UnauthorizedAccessException`.
If even one item is left, the journal stays; the journal is deleted only when everything is gone.
Dispose does not throw that failure.

#### Recover returns delete failures

When a delete fails, Recover rethrows that exception.
The journal stays.

### Why the same directory, and the trade-off

`.txnew` is placed in the same directory as the target file so that the rename at commit always stays within one volume (and on SMB is the fastest, metadata-only operation).
On the other hand, while a transaction runs, external tools such as Explorer can see the intermediate files (this is within the "dirty reads are allowed" premise, but the risk that someone deletes or edits them by mistake is not zero).

## API design

### How changes are detected

An operation log.
Only operations made by explicitly calling the library API (Add/Update/Delete/DeleteTree/Move/Copy/CreateDirectory) are tracked.
Scanning the whole work folder to detect differences automatically (snapshot comparison) is not used, because a full scan on a network file system conflicts with the non-functional requirements.
Only directory copy and Import, and ZIP creation and Export from a directory, walk the target tree to read it.

Each API section has, in this order: Overview, Preconditions, Locks, Journal, Exceptions, Cleanup on failure.
A heading that does not apply says "None".
The lock order and waiting, how the journal is written, and the commit steps are written once in "Concurrency and locks" and "Journal and recovery"; API sections only add what is specific to that API and refer to those sections.

### AddAsync / UpdateAsync

`AddAsync(path, Stream content, IProgress<TransferProgress>? progress, CancellationToken)`

`UpdateAsync(path, Stream content, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Separate, explicit APIs.
The operation kinds are Add and Update.
`progress` is optional; when null, nothing is reported.

#### Preconditions

This removes the IO for checking existence in advance, and detects early when the caller's intent and the file system disagree.

#### Locks

The shared work-folder lock, the path lock on the target path, and shared intent locks on the ancestors ("Lock granularity", "Intent locks").

#### Journal

The operation is written to the journal before `.txnew` is written ("Created directories").

#### Exceptions

Types specific to this API are not listed here.
Common ones are in "Common policy".

#### Cleanup on failure

How to undo a failure while creating something or writing `.txnew` is in "Created directories".

### DeleteAsync

`DeleteAsync(path, CancellationToken)`

#### Overview

Both files and directories.
The public operation kind is `Delete` for both (no separate kind for directories).
To delete everything under a directory, use `DeleteTreeAsync`.

#### Preconditions

The journal keeps internally that the target was a directory, and the commit check fails if it has been swapped.
A delete that looks only at direct children may delete an empty directory.
The directory stays on disk until commit.
The delete at commit still looks only at direct children.
Only direct children are checked.
For a nested tree, `DeleteAsync` the child directories first, then delete the parent (one `DeleteAsync` does not delete descendants recursively).
It is an error when a direct child (file or directory) is not one of: a scheduled delete of this transaction, a cancelled Add, or a Move out of the directory.
A parent with an Update, a remaining Add, or a `CreateDirectory` is an error too.
An Add / Update / Move / `CreateDirectory` into a directory scheduled for deletion is an error, and so is making that directory itself the Move destination.
This matches the operation-log idea of always stating explicitly "what is being changed", and avoids pulling things in implicitly.
The scan of direct children excludes this transaction's `.txnew` files.
Leftover sidecars of another transaction, and any other entry, are untracked and cause an error.

#### Locks

When the target is a directory, the transaction holds that directory's intent lock exclusively until it ends ("Intent locks").
The work-folder lock stays shared.
For a file: the shared work-folder lock, the path lock on the target path, and shared intent locks on the ancestors ("Lock granularity").

#### Journal

Only the scheduled delete is recorded; the actual delete happens at commit.
A directory is deleted with non-recursive `Directory.Delete` (after its children are gone).
When it is written: "Appending to the journal" and "Commit steps".

#### Exceptions

A swapped target, or direct children that do not meet the conditions, make the operation `Failed` in the commit check ("Handling external interference at commit", "Result details").
Misuse throws `InvalidOperationException` ("Common policy").

#### Cleanup on failure

Staging does not delete anything on disk.
A failure before commit does not touch anything on disk ("Handling external interference at commit").

### DeleteTreeAsync

`DeleteTreeAsync(path, CancellationToken)`

#### Overview

Schedules a directory and everything under it as one entry of kind `DeleteTree`.
An empty directory can be scheduled.
Staging does not walk the tree.
At commit: `Directory.Delete(path, recursive: true)`.

#### Preconditions

`InvalidOperationException` when this transaction has an operation under the directory.
If the path is exactly the source or destination of a scheduled directory Move, the Move is removed and replaced by a `DeleteTree` of the original directory.
A full delete of something under such a directory is rejected.

#### Locks

The work-folder lock stays shared, and the transaction holds that directory's intent lock exclusively until it ends ("Intent locks").

#### Journal

One `DeleteTree` entry.
Children are not written.

#### Exceptions

`UnsupportedOperationException` for a file.

| Type | Condition |
| --- | --- |
| `UnsupportedOperationException` | A file |
| `InvalidOperationException` | This transaction has an operation under the directory. A full delete under such a directory |

#### Cleanup on failure

After a crash: if the directory is still there, delete it again; if it is gone, it is applied; if it was swapped for a file, `ConflictDetected`.
Leftovers inside are deleted too, so the caller runs `RecoverAsync` first.

### MoveAsync

`MoveAsync(oldPath, newPath, CancellationToken)`

`overwrite` is false.
With `overwrite` true between two files: "Replacing a file".
With `overwrite` true when at least one of the source and destination is a directory: "Swapping a directory".

#### Overview

Moves a file or directory within the same volume.
A move across volumes is an error (no implicit fallback to copy + delete).
A directory is renamed once at commit, and its contents go with it.
Children are not written to the journal, and the tree is not walked.
The check looks only at existence.

#### Preconditions

When the normalized absolute paths are equal ignoring case (only the spelling differs, or the strings are identical), neither files nor directories are written to the journal, and no lock is taken.
The destination is accepted only when it is free, or is already the source of another Move in this transaction ("Move chains").
An operation under the directory itself, or under the source or destination, throws `InvalidOperationException`.
Consecutive Moves of a directory itself, and a Delete after them, fold the same way as file Moves; the Delete keeps the direct-children rule.

#### Locks

For a directory, the work-folder lock stays shared, and the transaction holds the intent locks on the source and destination exclusively until it ends.
For a file: the shared work-folder lock, the path locks on both paths, and shared intent locks on the ancestors.
A Move to the same path takes no lock.
The order is in "Locks for Move".

#### Journal

Only "old path → new path" is recorded; the rename happens at commit.
When `overwrite` is false, no replacement is written.

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidOperationException` | The normalized absolute paths are equal ignoring case. An operation under the directory itself, or under the source or destination |
| Across volumes | An error (no implicit fallback to copy + delete; "Move constraints") |

#### Cleanup on failure

Staging does not rename.

### Replacing a file

`MoveAsync(oldPath, newPath, bool overwrite, CancellationToken)` with `overwrite` true, where both the source and destination are files.

#### Overview

Replaces a file that already exists at the destination.
The existing `MoveAsync(oldPath, newPath, CancellationToken)` means false.
At commit, the replacement is one rename with `MOVEFILE_REPLACE_EXISTING` (like `File.Move(overwrite: true)`, without the flag that allows a copy), and the destination name never disappears, even briefly.
No bytes are copied.
The old file at the destination is gone, and the source file (contents, attributes, ACL) takes the destination name (it is a rename, so unlike Update the destination's ACL is not kept).
If the destination does not exist, this is a normal Move, and no replacement is written to the journal.
When the destination is a directory, or the source is a directory, "Swapping a directory" applies (replacement between files is what follows here).

#### Preconditions

If this transaction has a file Delete at the destination, that Delete is removed and folded into the replacing Move.
If the destination has any other operation (Add / Update / source or destination of another Move / under a DeleteTree or directory Move), `InvalidOperationException`.
Later operations on the source or destination of a replacing Move (Delete / Move / Update / Add / writing text and JSON / destination of Copy and Import) throw `InvalidOperationException` (how the replaced old file is handled would depend on folding, so they are not accepted for now).
The check requires the destination to be a file or absent (`ReplacedByFile` if it is a directory); `DestBefore` is its state at that time, and `DestAfter` is the source's state.
Apply and Recover check Before / After like any other Move.

#### Locks

The same as a normal file Move (the shared work-folder lock, the path locks on both paths, and shared intent locks on the ancestors).

#### Journal

The Move in the journal gets `overwrite: true`.
If the destination does not exist, no replacement is written.

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidOperationException` | The destination has an operation other than a file Delete. A later operation on the source or destination of the replacement |
| `ReplacedByFile` | The destination is a directory at the check |

#### Cleanup on failure

Staging does not rename.
Apply and Recover check the same way as other Moves.

### Swapping a directory

`MoveAsync(oldPath, newPath, overwrite: true)` where at least one of the source and destination is a directory.

#### Overview

Swaps the existing file or directory at the destination for the source.
The kinds may differ (a file for a directory, or a directory for a file).
If the destination does not exist, this is a normal Move.
If the destination is a file and the source is a file too, it is the single rename above.
The commit applies it in one operation: (1) rename the destination to `{destination name}.{txid}.txold` in the same parent, (2) rename the source to the destination, (3) delete `.txold` (`Directory.Delete(recursive: true)` for a directory, or the file for a file).
The destination name is missing only between (1) and (2).

#### Preconditions

Before is that the source and destination each exist with their kind; After is that the source is gone and the destination has the same kind as the source (for a directory, only existence is checked).
Apply and Recover tell the intermediate step from whether `.txold` exists, and continue from there (source and `.txold` exist and destination is missing: from (2); source is missing and destination and `.txold` exist: from (3); neither source nor `.txold` exists and the destination does: applied).
The source may be a directory that this transaction's `CopyAsync` / `ImportAsync` / `ExtractArchiveAsync` / `ImportArchiveAsync` created (created directories).
Then this transaction's Adds under the source are applied before the Move, and then the swap happens (`ImportAsync(external, "site.new")` followed by `MoveAsync("site.new", "site", overwrite: true)` swaps in one transaction).
`InvalidOperationException` when there is an operation other than Add under the source, or when there is any operation under a source that is not a created directory.
In Recover, if the swap is applied, the Adds under its source are treated as applied too.
If this transaction has a `DeleteTree` at the destination, that `DeleteTree` is removed and folded into the swap.
`InvalidOperationException` when this transaction has an operation under the destination.
Later operations on the source or destination of the swap, or under them, throw `InvalidOperationException`.
In the post-commit view, nothing exists under a destination that was swapped for a file.

#### Locks

The same as a normal directory Move (the intent locks on the source and destination, exclusive until the end).
The work-folder lock stays shared ("Intent locks").

#### Journal

The Move in the journal gets `overwrite: true`.

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidOperationException` | An operation other than Add under the source. Any operation under a source that is not a created directory. An operation of this transaction under the destination. A later operation on the source or destination of the swap, or under them |

#### Cleanup on failure

Discard and rollback cleanup do not delete `.txold` (in the middle of a swap, it is the destination's original).

### Changing the kind at a path

#### Overview

To change a file into a directory (or the other way round), prepare the new one under another name (`ImportAsync`, `CopyAsync`, extract, or `AddAsync` of a file with another name) and swap it in with an `overwrite` Move.

#### Preconditions

`CreateDirectoryAsync(x)` after `Delete(x)` is not accepted, because `CreateDirectoryAsync` creates the directory at the real path when staged.
`AddAsync(x)` after `Delete(x)` of an empty directory is not accepted either, because folding turns it into an Update, which fails at commit.

#### Locks

None.
The operations that bring the new one in follow the locks in their own sections.

#### Journal

None.
The swap itself is in "Swapping a directory".

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidOperationException` | `CreateDirectoryAsync(x)` after `Delete(x)`. `AddAsync(x)` after `Delete(x)` of an empty directory |

#### Cleanup on failure

None.

### CreateDirectoryAsync

`CreateDirectoryAsync(path, CancellationToken)`

#### Overview

Creates an empty directory at the real path when called.
Its contents are written with the plain file API.
The writer may be the same process or another process.
The journal has one entry of kind `CreateDirectory`, and the tree under it is not walked.
Files written with the plain file API do not appear in `GetPendingChanges`.
There is no progress.
The parent is not created.

#### Preconditions

Under it, the same operations work as under a directory whose parent existed from the start (Add / Update / Delete / DeleteTree / Move / Copy / Import / nested `CreateDirectory` / file `ReadAsync` / `ExportAsync` / text and JSON).
Each operation follows its existing rules.
`GetPendingChanges` shows this entry and the operations under it.
Delete / DeleteTree / source or destination of a Move / Update / a second `CreateDirectory` on the path itself throw `InvalidOperationException` and are not folded.
The path may be a copy source only when this transaction has no operation under it.
When it has one, `InvalidOperationException`, as with `CopyAsync`.
The `CreateDirectory` entry itself does not count as an operation under the path.
When there is no operation, the walk does not include uncommitted Adds.
`ExportAsync` is allowed.
It cannot be a Move source.
A copy or Import whose destination is the path itself fails, because the destination already exists.
Operations outside the tree, and sibling `CreateDirectory` calls, can continue in the same transaction.
At commit, the directory's contents are not examined; if the directory is still there, it succeeds and stays in place.
It is not renamed.
Operations under it are applied in the usual order.
If it is missing or has become a file, it is `Failed` before apply.
It is applied in the same group as Add / Move, and apply only checks existence.
If, after apply has started, the directory is missing or is a file, that operation continues as `PartialConflict`.
A `DeleteTree` or directory Move of an ancestor fails, because this operation is under it.

#### Locks

The path lock on that directory; the intent lock is not exclusive ("Intent locks").
The work-folder lock stays shared.
The shared lock is held until the transaction ends.
Operations under it take their own path locks and shared intent locks on the ancestors.

#### Journal

Write the entry to the journal as not created (`directoryCreated` false), create the directory, then write the journal again as created (`directoryCreated` true).
A journal without `directoryCreated` is read as not created.

#### Exceptions

Fails when the path exists at call time, the parent does not exist, the path is the work folder itself, or it is under `.txfio`.
Delete / DeleteTree / source or destination of a Move / Update / a second `CreateDirectory` on the path itself throw `InvalidOperationException`.
`InvalidOperationException` when the copy source has an operation.

#### Cleanup on failure

If creating the directory or the second write fails, the created directory and its entry are removed.
After a crash between recording and creating, if the directory does not exist, rollback's delete does nothing.
Discard, and `RecoverAsync` of a journal without `Committing`, call `Directory.Delete(path, recursive: true)` when it is recorded as created.
The `.txnew` files inside, whatever the plain file API wrote, and subdirectories a copy created are deleted too.
If `RecoverAsync` finds it still recorded as not created (the crash was around the create), it cannot say this transaction created it (after the crash, the plain file API may have created a directory with the same name and put contents in it), so it deletes it without recursion only when empty, and keeps it when it has contents.
Files that existed before the transaction and were moved inside with the plain API are deleted by discard too.
A nested `CreateDirectory` is deleted together with its parent.
If it is already gone, nothing happens.
An exception from a failed delete reaches the caller, and if the journal remains, the next `RecoverAsync` does the same delete.
After a crash after `Committing`, the directory is kept if it exists, and the operations under it follow the usual recovery.
If it is missing or is a file: `ConflictDetected`.

### CopyAsync

`CopyAsync(source, dest, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Copies a file or directory inside the work folder.
The source stays.
Bytes are copied even on the same volume.
For a file, the Add is written to the journal, then `.txnew` is written, and it stays as an Add.
For a directory, the tree on disk is walked, each file becomes a `.txnew` at the destination, and empty subdirectories are created too.
What stays in the journal is an Add per file; no kind for empty directories is added.
`progress` is optional; when null, nothing is reported.

#### Preconditions

This transaction's `.txnew` files are excluded.
Uncommitted Adds are not on disk under their real names, so they are not included.
Junctions and symbolic links are not followed, and those entries are not copied.
If the destination has a file or directory, it fails; nothing is merged.
This operation creates the destination itself and its empty subdirectories, and fails when the parent does not exist.
`InvalidOperationException` for the same path, a destination under the source, an operation of this transaction under the source or destination, the path itself already having an operation, or a copy under a scheduled `DeleteTree`.

#### Locks

Even for a directory copy, the work-folder lock stays shared.
The destination's intent lock is exclusive until the end.
The source's intent lock is exclusive only during the call, and is closed when the method returns ("Intent locks").
A file copy locks the source and destination in addition to the shared work-folder lock.
Ancestor intent locks are shared.

#### Journal

The directories to create and the Adds are written to the journal before anything is created ("Created directories").

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidOperationException` | The same path, a destination under the source, an operation of this transaction under the source or destination, the path itself already having an operation, a copy under a scheduled `DeleteTree` |

#### Cleanup on failure

On failure or cancellation, the half-written `.txnew` and the directories this operation created are deleted.

### ImportAsync

`ImportAsync(externalPath, targetPath, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Copies a file or directory outside the work folder into `.txnew` files by the same rules as `CopyAsync`, and keeps them as Adds.
The source is not deleted.
It is copied even on the same volume.
`progress` is optional; when null, nothing is reported.

#### Preconditions

Directories are created in the same order as `CopyAsync` ("Created directories").

#### Locks

Even for a directory the work-folder lock stays shared, and the intent lock on the import destination is exclusive until the end.
For a file, only the destination is locked, and the work-folder lock is shared.
Ancestor intent locks are shared.

#### Journal

The same as `CopyAsync` ("Created directories").

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentException` | The source is inside the work folder |

#### Cleanup on failure

On failure or cancellation, the half-written `.txnew` and the directories this operation created are deleted.

### ExportAsync

`ExportAsync(path, externalPath, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Copies out of the work folder: for a file, the same bytes as `ReadAsync`; for a directory, each file under it with the same bytes as `ReadAsync`.
Nothing is written to the journal, no file in the work folder changes, and nothing is locked.
Nothing is locked even for a directory.
`progress` is optional; when null, nothing is reported.

#### Preconditions

It neither overwrites nor creates the destination's parent.
This operation creates the destination directory itself and its empty subdirectories.
Junctions and symbolic links are not followed, and those entries are not copied.

#### Locks

None.

#### Journal

None.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentException` | The destination is inside the work folder |
| `ExternalConflictException` | A file or directory exists outside, or the parent does not exist |

#### Cleanup on failure

Partial output is deleted on failure or cancellation; files and directories that succeeded stay even after Dispose.

### ReadAsync

`ReadAsync(path, CancellationToken)`

#### Overview

Returns the file in the "post-commit view" as a read stream at position 0.
The caller disposes it.
The whole file is not copied to memory.
Directories are not supported.
It opens with `FileShare.Read | FileShare.Delete`, so the commit's rename proceeds even before the stream is closed.
Restaging the same path may fail until the stream is closed.
Cancellation is honored only at the start of the call.
When `detectExternalChanges` is true, the size and last write time (UTC) of the real file that was opened are recorded in the transaction's memory ("External changes after staging").
Reading this transaction's `.txnew` does not update that record.

#### Preconditions

How the view is decided: "Post-commit view".

#### Locks

No lock is taken, and nothing is written to the journal.

#### Journal

None.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ExternalConflictException` | No file in the view |
| Not supported | A directory |

#### Cleanup on failure

None.

### ExistsAsync

`ExistsAsync(path, CancellationToken)`

#### Overview

True if a file or directory exists in the "post-commit view", false if not.
Not existing is not an exception.
It does not fail just because the path is a directory.
Cancellation is honored only at the start of the call.

#### Preconditions

How the view is decided: "Post-commit view".

#### Locks

No lock is taken, and nothing is written to the journal.

#### Journal

None.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentException` | The path is outside the work folder |
| `InvalidOperationException` | Under the metadata folder, or overlapping calls |

#### Cleanup on failure

None.

### GetEntriesAsync

`GetEntriesAsync(directoryPath, CancellationToken)`

#### Overview

Returns the files and directories directly under a directory in the "post-commit view".
The result is a list of `DirectoryEntry` (absolute `Path` and `IsDirectory`), in lexical order ignoring case.
It does not recurse.
It enumerates the direct children once and checks existence for each candidate, so IO grows with the number of direct children.

#### Preconditions

Candidates come from the direct children on disk (the real directory behind the view; for the destination of a directory Move, its source), and from this transaction's operations whose parent is that directory (Add, Update, Move destination, `CreateDirectory`); each is checked in the "post-commit view".
This transaction's `.txnew`, `.txnew.prev`, and `.txold` are not included.
Staging files of another transaction are on disk, so they are visible (dirty read).
Path resolution and exceptions are the same as `ExistsAsync`.

#### Locks

No lock is taken, and nothing is written to the journal.

#### Journal

None.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ExternalConflictException` | No directory in the view |
| `UnsupportedOperationException` | A file |
| Same as `ExistsAsync` | Path resolution |

#### Cleanup on failure

None.

### GetPendingChanges

`GetPendingChanges()`

#### Overview

Returns the current journal contents (a list of Add/Update/Delete/DeleteTree/Move/CreateDirectory).
A directory copy appears as an Add per file.
The list of created directories is not included.
Operations scheduled under a `CreateDirectory` appear as those operations.
Files written with the plain file API do not appear.
The cost is almost zero (it just returns the journal); it is the equivalent of git status, for debugging and UI.
Creating a ZIP appears as one Add, and extracting as an Add per file.
`ExportArchiveAsync` does not appear.

#### Preconditions

None.

#### Locks

None.
Overlapping calls: "The same instance".

#### Journal

None.
It only reads and never writes.

#### Exceptions

Overlapping calls throw `InvalidOperationException` ("The same instance").

#### Cleanup on failure

None.

### ZIP archives

These are `ITransaction` methods that wrap `System.IO.Compression.ZipArchive`.
They are not extension methods,
because cleanup on failure, the work-folder lock, and locks need internal machinery.
The whole ZIP is never built in memory or in a temporary file; it is written directly to `.txnew` or to the external ZIP.
Only ZIP is supported; tar, bare GZip, and Brotli are out of scope.
Inside and outside are distinguished the same way as `CopyAsync` / `ImportAsync` / `ExportAsync`; passing a path on the wrong side throws `ArgumentException`.
When omitted, `compressionLevel` is `Optimal`, `includeBaseDirectory` is false (not included), and `entryNameEncoding` and `progress` are null.

### CreateArchiveAsync

`CreateArchiveAsync(source, archivePath, CompressionLevel compressionLevel, bool includeBaseDirectory, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Turns a file or directory inside the work folder into a ZIP inside the work folder.
The ZIP is written to `.txnew` and kept as one Add.
Input is read the same way as `CopyAsync`, walking the tree on disk.
This transaction's `.txnew` files are excluded, and uncommitted Adds are not included.
Junctions and symbolic links are not followed, and are not put in the ZIP.
Empty subdirectories are added as directory entries.
For a file, there is one entry with the file name, and `includeBaseDirectory` is ignored.
The entry name separator is `/`, and the encoding is fixed to the .NET default (UTF-8 for names that contain non-ASCII characters).
The entry time is the last write time of the file that was read.
It does not overwrite.

#### Preconditions

The overload that takes a list is in "Creating from a list".

#### Locks

The shared work-folder lock, plus the input and ZIP paths.
The intent lock on an input directory is exclusive only during the call, and is closed when the method returns.
Ancestor intent locks are shared.

#### Journal

Kept as one Add.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ExternalConflictException` | A file or directory exists at the ZIP path, or the parent does not exist |
| `InvalidOperationException` | The ZIP path is under the input directory, this transaction has an operation under the input or at the ZIP path, the path itself already has an operation, or the ZIP is created under a scheduled `DeleteTree` |

#### Cleanup on failure

On failure or cancellation, the half-written `.txnew` is deleted.

### ExportArchiveAsync

`ExportArchiveAsync(source, externalArchivePath, CompressionLevel compressionLevel, bool includeBaseDirectory, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Turns a file or directory inside the work folder into a ZIP outside the work folder.
Input is read the same way as `ExportAsync`: for a file, the same bytes as `ReadAsync`; for a directory, each file under it with the same bytes as `ReadAsync`.
Nothing is written to the journal, no file in the work folder changes, and nothing is locked.
Entries and arguments are the same as `CreateArchiveAsync`.
It neither overwrites nor creates the parent.

#### Preconditions

None.

#### Locks

None.

#### Journal

None.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentException` | The ZIP path is inside the work folder |
| `ExternalConflictException` | A file or directory exists outside, or the parent does not exist |

#### Cleanup on failure

A partial ZIP is deleted on failure or cancellation; a ZIP that succeeded stays even after Dispose.

### ExtractArchiveAsync

`ExtractArchiveAsync(archivePath, destinationDir, Encoding? entryNameEncoding, long? maxExtractedBytes, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Extracts a ZIP inside the work folder into a new directory inside the work folder.
The ZIP is read with the same bytes as `ReadAsync`, so an uncommitted ZIP created in the same transaction can be extracted too.
Each file is written to `.txnew` and kept as an Add per file.
This operation creates the destination directory itself and the directories in the entries, as `CopyAsync` does, and no kind for empty directories is added.
The last write time of each extracted file is set to the entry time, and the Add's After is taken from the `.txnew` with that time set.
`entryNameEncoding` is how entry names without the UTF-8 flag (such as Shift_JIS) are read; when omitted, the .NET default.
It does not merge or overwrite.

#### Preconditions

Name checks: "Extract checks".
The size limit: "Extracted size".

#### Locks

After the shared work-folder lock, the destination is locked, and its intent lock is exclusive until the end.

#### Journal

The directories to create and the Adds are written to the journal before anything is created ("Created directories").

#### Exceptions

| Type | Condition |
| --- | --- |
| `ExternalConflictException` | A file or directory exists at the destination, or the parent does not exist |
| `InvalidOperationException` | This transaction has an operation under the destination, the path itself already has an operation, or the extract is under a scheduled `DeleteTree` |

#### Cleanup on failure

On failure or cancellation, the half-written `.txnew` files, the Adds this operation added, and the directories this operation created are deleted.

### ImportArchiveAsync

`ImportArchiveAsync(externalArchivePath, destinationDir, Encoding? entryNameEncoding, long? maxExtractedBytes, IProgress<TransferProgress>? progress, CancellationToken)`

#### Overview

Extracts a ZIP outside the work folder into a new directory inside the work folder, by the same rules as `ExtractArchiveAsync`.
The ZIP is not deleted.

#### Preconditions

Extract checks and size are the same as `ExtractArchiveAsync`.

#### Locks

The same as `ExtractArchiveAsync`.

#### Journal

The same as `ExtractArchiveAsync`.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentException` | The ZIP path is inside the work folder |
| `ExternalConflictException` | The external ZIP does not exist |

#### Cleanup on failure

The same as `ExtractArchiveAsync`.

### Extract checks

#### Overview

Extract (`ExtractArchiveAsync` / `ImportArchiveAsync`) checks every entry name in the central directory before it starts writing.
If even one matches any of the following, nothing is staged, the destination is not created, and it fails with `InvalidDataException`:
names that leave the destination (`..`, absolute paths, drive letters), names that are not valid in a Windows path, names ending in `.txnew`, duplicate names (compared after folding with `ToUpperInvariant`, so names that differ only in case are duplicates too), and a name that appears as both a file and a directory.
When .NET cannot read the ZIP itself (it is corrupt, encrypted, and so on), the `System.IO.Compression` exception propagates as is.

#### Preconditions

None.

#### Locks

None.
The check runs before staging.

#### Journal

Nothing is written when a name matches.

#### Exceptions

| Type | Condition |
| --- | --- |
| `InvalidDataException` | Any of the names above |
| `System.IO.Compression` exceptions | .NET cannot read the ZIP itself (corrupt, encrypted, and so on) |

#### Cleanup on failure

Nothing is staged, and the destination is not created.

### Extracted size

#### Overview

`maxExtractedBytes` is the limit on the total number of bytes after extraction; when omitted, null (no limit).
If the sum of `Length` (the extracted size in the central directory) of the file entries exceeds the limit, then, as with the name checks, nothing is staged, the destination is not created, and it fails with `InvalidDataException`.
`InvalidDataException` also when the sum does not fit in a `long`, or when a file's `Length` is negative.
A negative limit is `ArgumentOutOfRangeException`.
With Deflate, .NET reads only up to `Length`, but with no compression (Stored) it may read more than `Length`.
If, while writing, the bytes actually read exceed the limit, it also throws `InvalidDataException`, and deletes the half-written `.txnew` files, the Adds this operation added, and the directories this operation created.
Pass a limit for ZIPs received from outside, so that a ZIP with a very high compression ratio (a so-called ZIP bomb) does not fill the shared disk.

#### Preconditions

None.

#### Locks

None.

#### Journal

Nothing is written when it fails before writing.

#### Exceptions

`InvalidDataException` and `ArgumentOutOfRangeException` above.

#### Cleanup on failure

As with the name checks, before writing, nothing is staged and the destination is not created.
When the limit is exceeded while writing, the half-written `.txnew` files, the Adds this operation added, and the directories this operation created are deleted.

### Creating from a list

`CreateArchiveAsync(IEnumerable<ArchiveEntrySource> entries, archivePath, CompressionLevel compressionLevel, IProgress<TransferProgress>? progress, CancellationToken)`, and `ExportArchiveAsync(entries, externalArchivePath, …)` with the same shape.
The overloads that take one path stay.

#### Overview

`ArchiveEntrySource(string SourcePath, string? EntryName = null)` is a public record. `SourcePath` is a file or directory inside the work folder, and `EntryName` is its name inside the ZIP.
There is no `includeBaseDirectory`.
`entries` is enumerated only once, first.
A directory element adds everything under it recursively, as the one-path overload does, and the entry names go under its name.
Empty subdirectories become directory entries.
Adding the same `SourcePath` twice with different entry names is allowed.
An empty list makes a ZIP without entries.
Entries in the ZIP follow the order of the list, and the contents of a directory follow the order in which they were enumerated at that position.
Progress is the total of bytes before compression, and `TotalBytes` is null.

#### Preconditions

When `EntryName` is omitted, it is the path relative to the work folder (separator `/`).
`\` becomes `/`.
Only for a directory element, `""` puts its contents at the root of the ZIP.
Reading is the same as the one-path overload.
Create reads the tree on disk, and throws `InvalidOperationException` when an element is already staged in this transaction, this transaction has an operation under a directory element, or an element is under a scheduled `DeleteTree`.
Export reads the same bytes as `ReadAsync`, and walks directories as `ExportAsync` does.

#### Locks

Create locks the shared work-folder lock plus each element and the ZIP path.
The intent lock on a directory element is exclusive only during the call, and is closed when the method returns.
Export does not lock.
Names that were passed are checked before locks and IO; names produced by walking directories are checked after locking and before writing.

#### Journal

Create writes one Add, as the one-path overload does.
Export writes nothing.

#### Exceptions

| Type | Condition |
| --- | --- |
| `ArgumentNullException` | `entries`, an element, or `SourcePath` is null |
| `ArgumentException` | `""` for a file element. Entry names are checked by the same rules as extract (names that leave the destination, names not valid in a Windows path, names ending in `.txnew`, duplicates ignoring case, a file and a directory with the same name), and a match throws |
| `InvalidOperationException` | Create reads the tree on disk, and an element is already staged in this transaction, this transaction has an operation under a directory element, or an element is under a scheduled `DeleteTree`. The ZIP path is the same as an element, or is under a directory element |

#### Cleanup on failure

When it fails in the latter case, no ZIP is left either.

### Post-commit view

`ReadAsync`, `ExistsAsync`, and text and JSON reads and writes work on the view after this transaction's scheduled changes are committed.
The whole work folder is not scanned; only the queried path and the list of operations are examined.

| Matching operation | View |
| --- | --- |
| `Add` / `Update` | Its `.txnew` |
| Destination of a file `Move` | The source's bytes |
| Source of a file `Move` | Missing. If there is an `Add` to the source, that `.txnew` |
| The same path is both the source and destination of `Move`s | The bytes after the chain is applied from its free end |
| `Delete` | The target is missing |
| `DeleteTree` | The target and everything under it are missing |
| Source of a directory `Move` | Everything under it is missing too |
| Under the destination of a directory `Move` | The corresponding path under the source. When the same directory is both a source and a destination in a chain, the tree after applying from the free end |
| Under a `CreateDirectory` | The existing rules of each operation, and the files the plain file API wrote, as they are |
| None of these | The file on disk |

`ReadAsync` of a directory itself is still not supported.
`ExistsAsync` returns a bool for a directory too.

### Text and JSON

These are `ITransaction` methods.
There are no synchronous versions.
They take no `IProgress`.
No method that only returns whether a file exists is added.
The implementation chooses between Add and Update.
The contents are loaded into memory once; reads use the same bytes as `ReadAsync`, and writes use the same staging as `AddAsync` / `UpdateAsync`.
For large byte sequences, use the `Stream` APIs.
Mocks on the caller's side implement these methods.

### ReadAllTextAsync / ReadAllLinesAsync

#### Overview

Turns the same bytes as `ReadAsync` into a string or an array of lines.
Line separators, and not including line breaks in the result, follow `File.ReadAllLinesAsync`.
A read without an encoding detects the BOM as `File` does.
There are overloads that take an encoding.

#### Preconditions

None.

#### Locks

The same as `ReadAsync`: none.

#### Journal

None.

#### Exceptions

For a directory, or a missing target, the same exceptions as `ReadAsync`.

#### Cleanup on failure

None.

### WriteAllTextAsync / WriteAllLinesAsync

#### Overview

`Add` if there is no file in the "post-commit view", `Update` if there is.
The destination of a `Move` gets `Update`.
For a file, it folds into an Add at the destination and a Delete of the original.
The source of a file `Move` gets `Add`.
Both the destination and the source of a directory `Move` throw `InvalidOperationException`.
Writing again in the same transaction rides on the existing restage (`Add` if it is still new, `Update` if it existed; after `Delete` the view has no file, so `Add` is chosen at that moment, but folding records `Update`).
A write without an encoding uses UTF-8 without a BOM.
Line breaks of `WriteAllLinesAsync` follow `File.WriteAllLinesAsync`.
If the content of `WriteAllTextAsync` is null, an empty string is written.
If the content of `WriteAllLinesAsync` is null, or the encoding argument is null, `ArgumentNullException`.

#### Preconditions

None.

#### Locks

The same as `AddAsync` / `UpdateAsync`.

#### Journal

The same as `AddAsync` / `UpdateAsync`.

#### Exceptions

`InvalidOperationException` and `ArgumentNullException` above.

#### Cleanup on failure

The same as `AddAsync` / `UpdateAsync`.

### ReadFromJsonAsync

#### Overview

`ReadFromJsonAsync<T>` deserializes the bytes of `ReadAsync` with `System.Text.Json`.
Failures are `System.Text.Json` exceptions as they are.
`JsonSerializerOptions` is optional; when omitted, the default.

#### Preconditions

None.

#### Locks

The same as `ReadAsync`: none.

#### Journal

None.

#### Exceptions

Failures are `System.Text.Json` exceptions as they are.

#### Cleanup on failure

None.

### AppendAllTextAsync / AppendAllLinesAsync

#### Overview

Appends to the end of the file in the "post-commit view".
If the view has no file, it writes a new one (Add), as `WriteAllTextAsync` / `WriteAllLinesAsync` do.
If it has one, it reads the view's contents into memory and writes the whole appended result by the same Add / Update rules as `WriteAllTextAsync` (the existing contents are loaded into memory too, so build large files with the `Stream` APIs).
Without an encoding, UTF-8 without a BOM.
No BOM is added to the appended part (only a new file gets the encoding's BOM, as with `File.AppendAllTextAsync`).
Line breaks follow `File.AppendAllLinesAsync`.
Null content is handled the same way as `WriteAllTextAsync` / `WriteAllLinesAsync`.

#### Preconditions

None.

#### Locks

The same as `WriteAllTextAsync`.

#### Journal

The same as `WriteAllTextAsync`.

#### Exceptions

The same as `WriteAllTextAsync` / `WriteAllLinesAsync`.

#### Cleanup on failure

The same as `WriteAllTextAsync`.

### WriteAsJsonAsync

#### Overview

`WriteAsJsonAsync<T>` writes the serialized JSON by the same Add / Update rules as `WriteAllTextAsync`.
`JsonSerializerOptions` is optional; when omitted, the default.

#### Preconditions

None.

#### Locks

The same as `WriteAllTextAsync`.

#### Journal

The same as `WriteAllTextAsync`.

#### Exceptions

The same as `WriteAllTextAsync`.

#### Cleanup on failure

The same as `WriteAllTextAsync`.

### Common policy

#### Writes are async only

All write APIs are asynchronous (`Task`-based).
Text and JSON reads and writes are the same, with no synchronous versions.
Since slow IO is the main target, async first is natural, and maintaining synchronous twins would cost more.

#### No IO on the caller's thread

This applies to the public async methods (`BeginAsync`, `RecoverAsync`, and the `ITransaction` members that return `Task` / `ValueTask`).

- After the overlapping-call check, and before the first IO, switch to the thread pool once
- Switch only when the caller has a `SynchronizationContext` (such as a UI thread) or a non-default `TaskScheduler`
- Do not switch when there is none (servers, consoles, already on the thread pool)
- Where the code resumes after completion is decided by the caller's `await` (called from UI, it returns to UI)
- Where .NET has an async API (stream reads and writes, the delay while waiting for a lock), use the async API
- Where it does not (opening lock files, rename, delete, existence checks, walking trees, applying the commit, flushing to disk), do it synchronously on the thread that was switched to
- Do not wrap each synchronous IO in `Task.Run` (that only adds round trips; not blocking the caller is achieved by the first switch)

#### Cancellation works only before apply

A `CancellationToken` is accepted, but it is effective only before the commit starts (up to the check phase).
Once the physical apply (running renames and deletes) starts, the `CancellationToken` is ignored and the commit runs to the end.
This clearly separates "an intentional stop" from "a stop by crash" (the former cannot happen during commit; only the latter is a target of `RecoverAsync()`).
When cancelled before the commit starts, `DisposeAsync()` runs the normal async rollback (delete `.txnew`, delete directories a copy created, delete `CreateDirectory` recursively, release locks, delete the journal).
If something is left, the journal stays and the next `RecoverAsync` cleans up.

#### APIs that report progress

`AddAsync` and `UpdateAsync`, `CopyAsync`, the copy into `.txnew` of Import, the external copy of Export, and the four ZIP methods take `IProgress<TransferProgress>?` just before the `CancellationToken`.
When null, nothing is reported.

`TransferProgress` is the number of bytes written so far, and the bytes remaining at the start (when the stream is seekable and its length can be read; null if the remainder is negative).

| Operation | Reports |
| --- | --- |
| All | Report each time 81920 bytes are written; empty content reports once at the end. The stream is not rewound |
| Directory copy | The total size is not measured in advance, `TotalBytes` is null, and the total of written bytes is reported. An empty directory reports 0 bytes once at the end |
| Add / Update | Only the copy of the caller's content into `.txnew`. The restage backup and journal writes are not included |
| ZIP create and Export | The total bytes read from the source files (before compression). `TotalBytes` is null. If empty, 0 bytes once at the end |
| Extract and Import | The total extracted bytes written to `.txnew`. `TotalBytes` is the sum of `Length` in the central directory. If empty, 0 bytes once at the end |

`Delete` / `DeleteTree` / `Move` / `CreateDirectory` / `Commit`, and progress by commit count, are out of scope.

#### Exceptions

Errors are exceptions. The base is `TxfioException`.
Commit success or failure is not an exception; it is returned as `CommitReport`. The overall result is `CommitResult` ("Result details").

| Type | When |
| --- | --- |
| `ExternalConflictException` | A precondition on disk no longer holds. Has one failed path. The target is missing, already exists, the destination is a directory, the parent or work folder is missing, a directory has an unexpected direct child, or the copy destination is occupied |
| `UnsupportedOperationException` | Not supported. `DeleteTreeAsync` on a file, a Move across volumes, `ReadAsync` of a directory |
| `InvalidOperationException` / `ArgumentException` | Misuse. Includes overlapping calls on the same transaction and reparse points inside the work folder |
| `InvalidDataException` | A dangerous entry name in a ZIP extract |
| `System.IO.Compression` exceptions | The ZIP itself cannot be read |
| `LockContentionException` | Another transaction holds the path, an intent lock on an ancestor directory, or the work folder. Has one failed path. No inner exception. The same exception after waiting until the deadline. Cancelling the wait is `OperationCanceledException` ("When contention is detected") |
| `RecoveryRequiredException` | `BeginAsync` and `CommitAsync` while an orphaned journal remains. `Path` is the work folder. No inner exception |

A readable journal is resolved with `RecoverAsync`.
A journal that cannot be read as JSON is not deleted, so it stays `RecoveryRequiredException` until someone fixes or deletes it.

### Transaction lifetime

- Expressed with the pattern `await using var tx = await Txfio.BeginAsync(path);`, as `IAsyncDisposable`.
  If disposed without Commit before `Committing` is written, it rolls back automatically.
  If apply throws after `Committing` is written, that exception is rethrown.
  Dispose does not roll back; it only closes the locks.
  The journal and `.txnew` files stay, and the next `RecoverAsync` rolls forward

### Directory operations

Each API's steps are defined in that API's section. This section only has the path rules shared by several APIs.

#### Delete and Move are in their own API sections

- A delete of direct children only is in "DeleteAsync". A delete of everything under a directory is in "DeleteTreeAsync". The direct-children condition and the exclusion of `.txnew` are there too
- Details of `CreateDirectoryAsync` are in its section
- Directory Move is in "MoveAsync". It does not walk the tree even if a crashed transaction's `.txnew` is inside, so the caller runs `RecoverAsync` first

#### The work folder itself and `.txfio` are not targets

The work folder itself cannot be deleted.
The metadata folder (`.txfio`) and everything under it are not targets of any write API (an error).

#### Reparse points inside are not followed

When resolving a path inside the work folder, if the target itself, or an ancestor other than the work folder itself, is a reparse point (junction, symbolic link, mount point), it is not followed, and `InvalidOperationException` is thrown.

- Even if the work folder itself was opened through a junction, the paths under it can be used
- If the path as written is outside the work folder, it stays `ArgumentException`
- It is not resolved to the final path. A junction that points back inside is rejected too
- When a directory copy, Import, Export, or ZIP creation walks a tree, child reparse points are still not followed, and those entries are not included
- If the root being walked, or a path named by the operation, is a reparse point, this rejection applies first

#### Missing parents are not created automatically

If a path whose parent directory does not exist is given, it is an error; the parent is not created.

The exceptions are below. A missing parent of the destination is still an error.

- `CopyAsync` and directory `ImportAsync` / `ExportAsync` create the destination directory itself and its empty subdirectories as part of the operation
- `ExtractArchiveAsync` / `ImportArchiveAsync` likewise create the destination directory itself and the directories in the entries
- `CreateDirectoryAsync` creates only the target empty directory, not its parent

## Concurrency and locks

### Supported range

Both several transactions running in parallel in one process, and concurrent access from several processes (other executables or other machines), are supported.
However, these locks are cooperative locks among Txfio users; they cannot prevent changes made with the plain file API (direct operations that do not go through Txfio)

### The same instance

The public members of one `ITransaction` cannot be called concurrently.
The call that entered first runs to the end, and a later overlapping call throws `InvalidOperationException` before it changes any state.
This includes `CommitAsync`, `DisposeAsync`, `GetPendingChanges`, `ReadAsync`, `ExistsAsync`, text and JSON reads and writes, and the write operations.
Calling the same instance from an `IProgress` in progress throws the same exception.
After a call finishes, calls can be made one at a time again.
The overlapping side records nothing.
What `ExtractArchiveAsync` reads inside its own operation is not an overlap of public members.
Concurrency between different transactions stays governed by the locks in this section

### Lock granularity

Only the paths an operation names are locked.

#### Path lock names

For both files and directories, the lock for a path is `.txfio/locks/{hex}.lock`.

- The hashed string is the path relative to the work folder, after the existing components are normalized to their long names
- Trailing names that do not exist yet are kept as they are
- The work folder itself is also normalized to its long name at begin and at recovery
- The separator is `\`, and the hash is SHA-256 of UTF-8 after folding with `ToUpperInvariant`
- Characters for which the NTFS upcase table and `ToUpperInvariant` differ do not map to the same lock

#### The work-folder lock

`Add` / `Update` / `Delete` / `DeleteTree` / `Move` / `Import` / `Copy` / `CreateDirectory` / `CreateArchive` / `ExtractArchive` / `ImportArchive` take the work-folder lock (relative path `.`) as shared before the path locks, and hold it until the transaction ends.

- `Read`, `Exists`, `Export`, and `ExportArchive` do not take the work-folder lock, even for directories
- Transactions that touch different paths can run in parallel while they share the work-folder lock
- Only `RecoverAsync` takes the work-folder lock exclusively
- Every operation holds the work-folder lock as shared; intent locks protect what is under a directory
- Scheduling under a directory: "Intent locks"

Previously, directory Move, `DeleteTree`, directory Copy and Import, `CreateDirectory`, ZIP creation from a directory, and extract took the work-folder lock exclusively during the call.
The shared work-folder lock is held until a transaction ends, so while any other transaction had staged even one item, all of these operations failed with `LockContentionException`; in effect they locked the whole work folder.

#### Locks per operation

| Operation | Takes |
| --- | --- |
| Delete of a directory | That directory's path lock and its exclusive intent lock. Child paths are not locked |
| `CreateDirectory` | That directory is locked too. Operations under it also take their own normal path locks |
| File `CopyAsync` | The source and destination, in addition to the shared work-folder lock |
| Directory `CopyAsync` | The source and destination. Children are not locked. The destination's intent lock is exclusive |
| Directory Import | The destination. Its intent lock is exclusive |
| `CreateArchiveAsync` | The input and ZIP paths, as with `CopyAsync` |
| `CreateArchiveAsync` from a list | Each element and the ZIP path. The intent lock on a directory element is exclusive only during the call |
| `ExtractArchiveAsync` / `ImportArchiveAsync` | The destination. Its intent lock is exclusive. The source ZIP and children are not locked |

### Intent locks

Another `.txfio/locks/{hex}.lock`, separate from the path lock.

#### How the file is made

- The hashed string is the same relative path as the path lock (separator `\`, `ToUpperInvariant`, SHA-256 of UTF-8) with `\*` appended
- `*` is not valid in a Windows path, so it never collides with a real relative path
- It is opened the same way as a path lock (`FileMode.OpenOrCreate`, `FileAccess.ReadWrite`). The file stays after the handle is closed
- Shared is `FileShare.ReadWrite`; exclusive is `FileShare.None`
- The ancestors are the directories inside the work folder on the way to the path's parent, not including the work folder itself, taken from the root down

#### Operations that lock a path hold their ancestors as shared

They take the intent lock of each ancestor as shared, and hold it until the transaction ends.

- `Read`, `Exists`, `Export`, and `ExportArchive` do not lock paths, so they take no intent locks either
- The extra opens are only the number of ancestors; the tree is not walked
- An intent lock that the same transaction already holds exclusively is not reopened. A shared request is satisfied by it too

#### Exclusive until the end

The intent lock of the directory being scheduled is taken exclusively and held until the transaction ends.
It is not given back when the method returns, when operations are folded, when a failure means nothing was recorded, or after cancellation.

- The target directory of `DeleteTreeAsync`
- The source and destination of a directory `MoveAsync`
- The destination of a directory `CopyAsync`, and the destination of a directory `ImportAsync`
- The destination of `ExtractArchiveAsync` and `ImportArchiveAsync`. The source ZIP is not scheduled
- A directory `DeleteAsync`. The delete at commit still looks only at direct children. An operation that stages a grandchild also opens this directory as an ancestor, so it conflicts

The copy source, and the input directory of ZIP creation, hold the intent lock exclusively only during the call, and close it when the method returns.
This stops other transactions from staging or committing under it while it is being read (a transaction that has staged under it holds that ancestor's intent lock as shared, so the exclusive lock cannot be taken and the call fails with `LockContentionException`, with `Path` set to that directory).
If this transaction already holds that intent lock exclusively, it keeps holding it without closing.
The bytes have been written to `.txnew` by the time the method returns.

#### Not exclusive

- `CreateDirectoryAsync` takes only the path lock on the target directory. Other transactions can stage under it
- `CreateArchiveAsync` from a directory (including a list that contains a directory) writes the bytes read during the call into the ZIP. The input directory's intent lock is not exclusive
- The same transaction touching what is under its own scheduled directory is a contract violation, and stays `InvalidOperationException` before any lock

#### Order

An operation that schedules a directory takes its intent lock exclusively while holding the shared work-folder lock.
If that fails, `LockContentionException`, with `Path` set to that directory.
The same exception when another transaction already holds something under it as shared.

The order is below. The waits share the one deadline from "When contention is detected".

1. The shared work-folder lock
2. The ancestors of each target (from the root down), in lexical order of the upper-cased absolute target paths
3. The exclusive intent lock of the directory being scheduled
4. Path locks

`RecoverAsync` holds the work-folder lock exclusively during its own call, and does nothing while the share-lost marker is in use.

### The share-lost marker

Just before letting go of the work-folder lock while still holding a path lock or intent lock, a transaction opens one `.txfio/share-lost.lock`.
It is not placed in `.txfio/locks/`.
It is opened with `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.ReadWrite`.
`DeleteOnClose` is not used,
because once a closed handle marks the file for deletion, it cannot be reopened while others still hold it.
Several transactions can hold it at the same time.
It is not opened when the transaction holds no path lock or intent lock.
The handle is closed after the work-folder lock is held again (shared or exclusive), or when all path locks and intent locks are closed.
Discard closes this handle together with the locks it holds, even without the work-folder lock.
If it cannot be opened, the work-folder lock is not let go, and that exception is returned.
While the work-folder lock is already lost, the handle opened before letting go is kept open.
When a process crashes, the OS closes the handle.
Even if the file remains, it is not in use when nobody holds it, because the exclusive check can open it.

After the exclusive work-folder lock is taken, the transaction first closes its own marker, then opens only this file with `FileMode.Open`, `FileAccess.ReadWrite`, `FileShare.None`.
`DeleteOnClose` is not used.
If the file does not exist, no transaction holds other locks without the work-folder lock.
If it opens, it is closed right away, and it is not in use.
On a sharing violation it is in use, so it is reopened until the deadline while holding the exclusive lock ("When contention is detected").
Failures other than a sharing violation are returned as that exception.
`.txfio/locks/` is not enumerated.
The wait does not depend on how many paths were touched in the past.
The handle used for the check is closed right away.
The path `.lock` files and intent lock files stay.
`RecoverAsync` deletes them ("Cleaning up lock files").

### When contention is detected

Pessimistic.
Misuse (path resolution, under the metadata folder, not supported, double staging) is reported first, and locks are taken after that.
`BeginAsync` itself takes neither path locks nor the work-folder lock.
The waiting happens in the public methods that take locks after that, and in `RecoverAsync`.
The lock wait is stored once per transaction.
`BeginAsync(path, TimeSpan lockWait, CancellationToken)` is added; the existing `BeginAsync(path, CancellationToken)` has `lockWait` of `TimeSpan.Zero`.
`RecoverAsync(path, TimeSpan lockWait, CancellationToken)` is added too; the existing `RecoverAsync(path, CancellationToken)` is also zero.
Negative values are `ArgumentOutOfRangeException`.
`Timeout.InfiniteTimeSpan` waits without a deadline and ends only on cancellation.
Operation methods do not get a wait argument.
Lock acquisition in the transaction uses the value `BeginAsync` stored.

Each public method call has one deadline: `lockWait` from the start of that call.
The work-folder lock, the share-lost marker check, intent locks, and path locks share that one deadline.
The next call waits from the same limit again.
It is not a running total for the whole transaction.
The first attempt opens without waiting.
On a sharing violation, it retries every 100 ms.
If less than 100 ms remains, it waits only for the remaining time and tries one last time.
The interval cannot be passed by the caller.
On SMB each attempt is a CreateFile that opens a lock file, so the library fixes the interval.
`TimeSpan.Zero` does not sleep and tries once, as before.
The sleep while waiting is cut short by that call's `CancellationToken`.
Then it throws `OperationCanceledException`, not `LockContentionException`.
Cancellation after the locks are taken works as each API already does (after the commit's apply starts, the token is not observed).

It waits only when it opens something intending to hold it as a lock and gets a sharing violation:
the shared work-folder lock, the exclusive work-folder lock, the share-lost marker check, intent locks, path locks, and the work-folder lock of `RecoverAsync`.
While waiting for the exclusive lock, it does not hold the work-folder lock.
If the exclusive lock cannot be taken by the deadline, it goes back to shared only when it held shared before, then throws `LockContentionException`.
The in-use check after the exclusive lock is taken reopens while holding the exclusive lock.
If the marker is not free by the deadline, it gives up the exclusive lock, goes back to shared, and throws `LockContentionException` (`Path` is the work folder).
The exclusive work-folder lock is held only while that public method runs.
It goes back to shared before returning.
The exclusive intent lock is held until the transaction ends ("Intent locks").
While waiting for a path lock or intent lock, the work-folder lock already taken and the locks taken earlier in lexical order are kept.
If it cannot be taken by the deadline, `LockContentionException`, with `Path` set to the path that could not be taken.
On cancellation, the locks already taken are kept too.
When cancelled while waiting for the exclusive lock, it goes back to shared only when it held shared before, then throws `OperationCanceledException`.
A sharing violation while going back is remembered as having lost the work-folder lock, and the same `OperationCanceledException` is thrown.
When it cannot go back for a reason other than a sharing violation, that exception is returned.

The liveness lock check of an orphaned journal does not wait.
IO failures other than a sharing violation do not wait either, and are returned as they are.
Paths and intent locks already held are not reopened.
The order of waiters is not guaranteed.
When a precondition on disk no longer holds after locking, or writing `.txnew` or the journal fails, the locks are kept until the transaction ends.
On a file server with slow IO, the Optimistic approach risks wasting work that has already been done right before commit, so Pessimistic is used.

### Locks for Move

Locks are taken on both the old and the new path (to prevent a conflict where another transaction writes to the destination).
The order is: the work-folder lock first, then lexical order of the upper-cased absolute paths (so `Move(A→B)` and `Move(B→A)` running at the same time do not deadlock).
If the second cannot be taken, the first is kept.
A Move to the same path takes no lock and throws `InvalidOperationException`.
A directory Move also keeps the work-folder lock shared, and takes the intent locks on the source and destination exclusively ("Intent locks").
The steps below, which raise the work-folder lock to exclusive, belong to `RecoverAsync`.
If shared is already held, it is closed once and exclusive is opened.
Reopening and the deadline follow "When contention is detected", and the work-folder lock is not held while waiting.
If exclusive cannot be taken by the deadline, it goes back to shared.
After opening, it checks the marker ("The share-lost marker").
If it is in use, it reopens until the deadline while holding exclusive.
If it is not free by the deadline, the operation is not recorded, it goes back to shared, and throws `LockContentionException` (`Path` is the work folder).
When shared cannot be reopened, the failure is not swallowed.
On a sharing violation, the same exception is thrown, and it remembers that the work-folder lock was lost and reopens it at the next lock acquisition.
Failures other than a sharing violation are returned as that exception.
The marker handle used for the check is closed right away, and the path `.lock` is not deleted.
The order of path locks and intent locks is in "Intent locks".
Operations do not make the work-folder lock exclusive, so there is nothing to give back when the method returns

### Lock mechanism and dead-process detection

A `.lock` file is not just created; it is implemented as an OS file-sharing lock that keeps a handle open with `FileShare.None`.
When a process crashes or is killed, the OS or SMB server releases the handle automatically.
When a test stops a commit partway, it also only closes the handles.
No leases, heartbeats, staleness checks, or races to take a lock over are needed.
Another transaction tries to take the lock: a sharing violation means "in use", and a successful open means "leftovers of a dead process".
However, the delay between a client disconnect and the SMB server releasing the handle depends on the SMB server implementation (Windows Server SMB shares, various NAS products, and so on), with no standard guarantee, so it must be verified on a real file server

### Tying locks to the journal

Releasing a lock means closing its handle.
When a process crashes, the OS closes the handles, and the Dispose after a test stops a commit partway also only closes handles.
The `.lock` files stay.
Recover holds the work-folder lock exclusively while it processes journals ("Recover API").
It does not open any other path `.lock` (the transaction liveness lock is opened as described below, and the marker as described in "The share-lost marker").
After the handle is closed, another transaction may take the same path's `.lock` before Recover does.
However, while an orphaned journal remains, that transaction can neither begin nor commit ("Rejecting while an orphaned journal exists"),
because the Before / After of the crashed transaction only checks existence for directories, so data committed under them after the crash could not be told apart.
`RecoverAsync` deletes the `.lock` files ("Cleaning up lock files" below)

### Cleaning up lock files

`.txfio/locks/*.lock` for path locks and intent locks grows by one per touched path and each of its ancestors.
`RecoverAsync` holds the work-folder lock exclusively, confirms that the marker ("The share-lost marker") is free, and deletes `*.lock` in `.txfio/locks/` before processing journals.
At that point no other transaction holds a path lock or intent lock (holding one requires the shared work-folder lock or the marker), so there is no race where someone has a deleted file open.
When an operation locks the same path after the delete, it recreates the file with `FileMode.OpenOrCreate`.
Files that cannot be deleted (sharing violation, access denied) are left and it continues.
The liveness locks (`tx-{guid}.lock`) and the marker (`share-lost.lock`) are not deleted.
Only one level of `.txfio/locks/` is enumerated; the work folder is not walked.
If `.txfio/` does not exist, nothing happens

### Transaction liveness lock

Apart from path locks, each transaction holds one `.txfio/tx-{guid}.lock`.
`{guid}` is the same as the journal `.txfio/tx-{guid}.journal`, and it is not placed in `.txfio/locks/`.
`BeginAsync` opens it with `FileMode.CreateNew`, `FileShare.None`, `FileOptions.DeleteOnClose` before writing the journal.
If it cannot be opened, the journal is not written and the exception is returned.
If the journal cannot be written, the liveness lock is closed.
The handle is held until the transaction ends.
On commit completion and on discard, it is closed after the journal is deleted.
It is closed even if deleting the journal fails.
The next `RecoverAsync` handles the remaining journal.
The Dispose after a test stops a commit partway closes only the handle and keeps the journal.
When a process crashes, the OS closes the handle, and `DeleteOnClose` deletes the file too.
If it is not deleted and remains, it does no harm

### Rejecting while an orphaned journal exists

An orphaned journal is a `.txfio/tx-{guid}.journal` whose liveness lock can be opened the same way Recover opens it (`FileMode.OpenOrCreate`, `FileShare.None`, `FileOptions.DeleteOnClose`), and whose journal still exists after the lock has been opened.
The check covers the whole work folder; it does not compare paths.
It lists `.txfio/*.journal`, then opens and immediately closes each liveness lock.
It does not read the journals.
A sharing violation means the transaction is alive.
Its own journal always gives a sharing violation, because it holds its own liveness lock.
Failures other than a sharing violation are returned as that exception.
The check happens in two places

- `BeginAsync`: before creating the liveness lock. If there is an orphan, it creates nothing and returns `RecoveryRequiredException`
- `CommitAsync`: when there is at least one operation, before checking preconditions and recording Before / After.
  If there is an orphan, it touches nothing on disk, does not write `Committing`, and returns `RecoveryRequiredException`.
  The transaction stays uncommitted, and discarding it rolls back.
  This transaction holds the shared work-folder lock, so call `RecoverAsync` after discarding it

Another transaction may crash after `BeginAsync` has finished, so the check at begin is not enough; it is also checked at commit.
The check at commit runs while holding the shared work-folder lock, so it never overlaps with `RecoverAsync`, which holds it exclusively.
A liveness lock can be opened by only one handle at a time, so while another transaction's check has the same orphan's liveness lock open, it is taken to be alive because of the sharing violation.
This very short window is accepted, with no retry (a live transaction always gives a sharing violation, and waiting would delay every commit).
It is not part of the lock wait ("When contention is detected") either

## Journal and recovery

### Physical layout

One hidden metadata folder (for example `.txfio/`) is created in the work folder, and the journals and lock files are kept there.
Journals and locks are small control files that are read and written often, so keeping them in one place is easier to manage, and lowers the risk that external processes mistake control files for data files.
Lock file names do not use the path relative to the work folder as is, but its hash (for example SHA-256),
to avoid hitting the Windows path length limit (MAX\_PATH = 260 characters) with deep paths.

### One journal per transaction

Each transaction has its own journal file (for example `.txfio/tx-{guid}.journal`).
Appending from several processes to one shared journal would need separate mutual exclusion and would be complex, so creating the file itself guarantees the uniqueness of the transaction.

### Journal paths

The `path`, `newPath`, and `stagingPath` of operations, and `createdDirectories`, are written as paths relative to the work folder.
The separator is that of the OS that wrote them.
As with lock keys, they point to the same place even when another machine opens the same share with a different drive letter or UNC path, or when the work folder is renamed or moved.
On reading, they are combined with the work folder passed to `RecoverAsync` (normalized to its long name) to make absolute paths.
Absolute paths that are written (journals of earlier versions) are used as they are.
If the combined result is outside the work folder, is the work folder itself, or is under `.txfio`, the journal is an "unreadable journal".
The document version stays 1 (there is no migration, since the library is not published yet).

### Operation kinds

`PendingChangeKind` is Add = 0, Update = 1, Delete = 2, Move = 3, DeleteTree = 4, CreateDirectory = 5.
There are no gaps.
The journal document version stays 1.
Documents with the Attach kind are not read or migrated.
Journals that cannot be read as JSON, have a different version, or have an unknown kind follow "Unreadable journals".

### Overwriting the journal

Both the first creation and later overwrites write to a temporary file in the same directory, `.txfio/tx-{guid}.journal.tmp`, flush it to disk (`Flush(flushToDisk: true)`), and then rename it to the journal path.

#### How it is replaced

- The first time uses `File.Move(overwrite: false)`. It fails if a file already exists at the journal path (the same as the earlier `FileMode.CreateNew`)
- Later writes replace it with `File.Move(overwrite: true)`
- The rename is within one directory, so the journal path holds either no file or a complete version
- A crash during the first write leaves no incomplete journal, and it is neither an orphan nor an "unreadable journal" (previously, a 0-byte journal from a crash right after creation blocked the work folder until someone deleted it)

#### When a temporary file remains

When writing the temporary file fails, or the rename fails, the temporary file is deleted and the exception is returned.

- When a crash leaves only the temporary file, the `RecoverAsync` that processes that journal deletes it after opening the liveness lock and before reading
- When there is no journal and only a temporary file remains (a crash during the first write), `RecoverAsync` lists `.txfio/tx-*.journal.tmp` while holding the work-folder lock exclusively, and deletes those whose liveness lock it can open (the owner is not alive) and that have no journal yet
- They are not counted in the result. A temporary file is not a journal, and is not counted as an orphan

### Appending to the journal

Changes that only add operations or created directories at the end of the table (most of `AddAsync`, file Copy and Import, directory import, `CreateDirectoryAsync`, and so on) add one line instead of rewriting the whole journal.

#### File format

- An append record is `{"append":[operations...],"createdDirectories":[...]}`
- The journal is JSON Lines. The first line is the document as before (`WriteIndented` is false, so it is one line), and later lines are append records
- Each record ends with a newline
- An append opens with `FileMode.Append`, flushes to disk (`Flush(flushToDisk: true)`), and closes
- The document version stays 1 (a journal without append records has the same shape as before)
- Adding n operations one at a time writes O(n) bytes, with no extra file creation or rename

#### When it is rewritten

When the middle of the table changes (folding, removed operations, rollback) and for `Committing`, it is rewritten as a one-line document with a temporary file and rename, as before (append records disappear).

#### Reading

The operations and created directories of the append records are added in order to the document on the first line.

- If the last line does not end with a newline, it is dropped as a crash during an append (the journal comes first and the `.txnew` files and created directories come after, so what that line points to has not been created yet)
- If a line that ends with a newline cannot be read, the journal is an "unreadable journal"

### Move chains

#### The same file folds into one

Consecutive Moves of the same file (`Move(A→tmp)` followed by `Move(tmp→B)`) fold into `Move(A→B)`.
A swap through a temporary name becomes a cycle with this folding, so it is not supported.

#### Different files stay as two

Chains of different files are not folded.
When the destination is the source of another Move, as with `Move(log.1→log.2)` and `Move(log→log.1)`, both stay.
A free end, like `log.2`, is needed.

#### Destinations accepted at staging

The destination is accepted only when it is free, or is already the source of another Move in this transaction.
Calls go from the free end. Files and directories alike.

- When the destination exists and is not the source of another Move, `ExternalConflictException`
- A Move that replaces an existing file happens only when `overwrite` is true (`overwrite` of `MoveAsync`)
- An `Add` to the source of a file Move is accepted. Even if the source is still on disk, the Add target is not treated as already existing
- An Add to the source of a directory Move is `InvalidOperationException`
- A cycle without a free end is not accepted at staging

#### Order of apply

Start from a Move whose destination is not the source of another Move, then continue with the Move that goes into its source.
An Add to a source comes after that Move. Other ordering is as before.

- Before / After are projected in this apply order. Recover after a crash uses the same order
- When it is found before commit that there is no end, the operation is `Failed`, and nothing on disk is touched
- A general dependency graph and topological sort remain future work
- Text and JSON writes follow the "post-commit view", and the source of a file Move becomes an Add

### Reusing the source of a file Move

After `Move(A→B)`, an operation on A is directed to the operation that decides A's "post-commit view".
That is an `Add` rewritten to A, or a `Move(Y→A)` coming into A from another Move.
It is not `Move(A→B)` itself.

- `Delete` of A: if there is a rewritten `Add`, it cancels it.
  If there is `Move(Y→A)`, it folds into a `Delete` of Y's original file.
  If there is neither, it folds into `Delete(A)` as before
- `Move(A→C)` with A as the source: the rewritten `Add` is moved to C.
  If there is `Move(Y→A)`, it folds into `Move(Y→C)`.
  If there is neither, there is no source, so `ExternalConflictException`
- `Update` to A: if there is `Move(Y→A)`, it folds the same way as an Update to that destination
- A `Move` whose destination is B throws `InvalidOperationException` if there is an `Add` rewritten to B, even when B is the source of another Move
- When a Move is removed and folded into "delete the original file" (an Update to the destination, or a Delete of the destination), if there is an `Add` rewritten to the source, it becomes an `Update` (the same as deleting the original and writing).
  When another Move is going to come into the source, the apply order (Move first, Delete after) cannot express it, so `InvalidOperationException`

### Commit steps (crash safety)

1. Write the `Committing` marker to the journal and fsync. Overwriting is in "Overwriting the journal"
2. Run the finishing step for each file.
   Multiple operations on the same path are normalized in advance, when they are recorded in the journal (for example: an `Add` followed by a `Delete` on the same path cancel each other; `Move(A→B)` followed by `Move(B→C)` folds into `Move(A→C)`; `Move(A→B)` followed by `Update(B)` folds into an `Add` at the destination and a `Delete` of the original; `Move(A→B)` followed by `Delete(A)` or `Delete(B)` folds into `Delete(A)`).
   For the normalized operations, only a simple order is supported: non-destructive operations first (new Add, Move, CreateDirectory), then Update, and last Delete and DeleteTree (deepest path first; a directory Delete is non-recursive and comes after file Deletes and Moves out; DeleteTree is `Directory.Delete(path, recursive: true)`).
   However, Move chains follow the order in "Move chains", and an Add to a source comes after that Move.
   A topological sort over a general dependency graph is a candidate for future work.
   For each operation, the Before just before apply and the After just after apply are written to the `Committing` journal.
   For files: existence, size, and last write time (UTC); a missing state is "missing" in both; for directories, only existence.
   A Move records both the source and the destination.
   The Before of Add / Update / Delete / DeleteTree / Move is taken at the check, and the After is decided at that point (Add / Update: `.txnew`; the Move destination: the source file; after a delete, the Move source, and DeleteTree: missing. The Before of DeleteTree is the directory's existence).
   Within one journal, they are projected in apply order, and the previous After becomes the next Before.
   The Before and After of `CreateDirectory` are also only the directory's existence; its contents are not examined.
   If it is missing or is a file at the check, the commit does not happen.
   Apply only checks existence and does not rename.
   When existence matches both Before and After, it is skipped as applied, and not recreated.
   The journal is not updated after apply.
   `Applying`/`Applied` are not written (the only fsync is for `Committing`)
3. When everything is done, delete the journal and release the locks

Each operation compares the current state of its target paths with `Before` (not applied) and `After` (applied).
Add / Update is not applied when `.txnew` remains and the target matches Before.
Even when Before and After are the same, `.txnew` is applied.
When `.txnew` is gone and the target matches After, or when `.txnew` remains but the target does not match Before and does match After, it is applied: `.txnew` is deleted and it is skipped.
Other operations, when the marker exists: if they match `After`, `.txnew` is deleted and they are skipped; if they match `Before`, they run again.
If they match neither, it is an external conflict (handled as in "Handling external interference at commit").
A path that a later operation in apply order also changes is not used to decide whether the earlier operation is applied.
When only one of the source and destination of a Move changes later (a Move in a chain, an Add rewritten to the source), the match with After is checked on the other one only.
When both change later, both are checked.
If it is not applied, the later operations are not applied yet either, so Before is still checked on all paths.
When the document is readable and the marker is absent, it is uncommitted and can be rolled back safely:
the operations' `.txnew` files and their `.prev`, a non-recursive delete of `createdDirectories` deepest first, and the recursive delete of `CreateDirectory` ("Created directories").
When deleting a created directory fails, the exception is rethrown and the journal stays.
When the document is not readable, "Unreadable journals" applies

### Handling external interference at commit (a consequence of allowing dirty reads)

1. Before writing the `Committing` marker, check the preconditions of every operation in the journal, and write Before / After at the same time.
   The Add target does not exist, the Update target is a file, the Delete target is a file or a directory that meets the direct-children conditions, the DeleteTree target is a directory, and the Move source is a file or directory and the destination does not exist.
   The Update target and the target of a file Delete must not have the read-only attribute (if they do, they are rejected with `ReadOnly`; on Windows both the replacement and the delete would throw `UnauthorizedAccessException` during apply and be finished as `PartialConflict`).
   Move can rename read-only files, so it is not checked.
   DeleteTree and directories are not checked, since their contents are not walked.
   When `detectExternalChanges` is false, an Update whose contents alone changed after staging does not fail here, and Before is the disk at that point.
   When it is true, "External changes after staging" applies: an Update, file Delete, or file Move that differs from the record is `Failed` with `ExternalChange`.
   `CreateDirectory` also fails if the directory does not exist, or has been swapped for a file.
   Its contents are not examined.
   If a precondition no longer holds, nothing on disk has been touched yet, except directories `CreateDirectory` has already created, so the whole commit is stopped safely and `Failed` is returned ("Result details").
   Discard after a failure deletes that directory with its contents, the same as Dispose of an uncommitted transaction
2. If external interference happens after the check, after the `Committing` marker is written, and after apply has started (very rare), there is no going back, by the roll-forward principle.
   The affected operation is skipped and the commit continues, and `CommitReport.Result` of `CommitAsync` makes the outcome clear (`Succeeded`: every operation was applied as expected / `PartialConflict`: an inconsistency from external interference was detected in some operations, but the commit was finished; it returns after deleting the journal and the `.txnew` of operations that were not applied / `Failed`: the check before commit failed, and nothing on disk was touched).
   Making `PartialConflict` an explicit enum value gives an API shape where callers are less likely to ignore the return value.
   The paths and reasons of rejected and skipped operations are in "Result details".
   After `Failed`, the same transaction can commit again.
   After `PartialConflict`, the same instance cannot retry.
   Sharing violations are not retried automatically

### External changes after staging

`BeginAsync(path, bool detectExternalChanges, CancellationToken)` and `BeginAsync(path, TimeSpan lockWait, bool detectExternalChanges, CancellationToken)` are added.
The existing `BeginAsync` has `detectExternalChanges` false.
When false, an Update whose contents alone changed after staging does not fail.

When true, file `Update`, file `Delete`, and the source of a file `Move` are checked.
`Add` and directories are not compared (a directory Delete / DeleteTree / Move does not look inside, so changes under it are not detected).
The record is the size and last write time (UTC), kept only in the transaction's memory.
It is not written to the journal.
Whether the transaction is discarded or committed, `RecoverAsync` does not look at this record.

Each `ReadAsync` of a real file updates the record to that moment.
When the post-commit view is this transaction's `.txnew`, the record is not updated; if the real file behind the view is not recorded yet, that real file is recorded.
When reading the destination of a file `Move`, it is the source's real file.
When there is no real file, nothing is recorded.
A path with a `Read` record is not updated when restaged.
An `Update` that has not been `Read` records the real file at staging time, and updates it at each restage.
A `Delete` of a file that has not been `Read`, and the source of a file `Move`, record the real file at staging time (if there is a `Read` record, that is used).
Even if folding changes a `Delete` or `Move` into another shape, the comparison uses the record of the original real file.
A path with no real file on disk is not recorded.
Text and JSON writes are `Update`s, so they behave the same.
Even if folding turns an `Update` into an `Add` and a `Delete`, the real file is compared if there is a record.

At the check before commit, if the recorded real file is still a file and either its size or last write time differs, the whole commit is `Failed` before `Committing` is written.
Nothing on disk is touched.
The reason is `ExternalChange`, and the path is that file.
Every differing `Update`, `Delete`, and `Move` is listed in `Operations`.
Operations whose kind changed by folding and that remain are listed too.
When the file is missing it stays `Missing`, and when it has become a directory it stays `ReplacedByFile`.
A rewrite with the same size and the same last write time is missed.
The granularity of the last write time differs by file system (NTFS 100 ns, FAT 2 seconds, and some SMB servers round to seconds), so a rewrite that keeps the same size within that granularity is missed.
A hash of the contents would mean reading the whole file at each comparison, which is expensive where IO is slow, so it is not used.

### Recover API

Nothing happens automatically.
Only when the application explicitly calls `RecoverAsync()`, at the time it opens the work folder, are the journals in `.txfio/` processed in lexical order ignoring case, and stale transactions detected.
Before listing the journals, it opens the work-folder lock exclusively.
The steps and waiting are the same as the exclusive work-folder lock that operations take ("When contention is detected"), using the `lockWait` passed to `RecoverAsync`.
When none is passed, `TimeSpan.Zero`.
When the marker is not free by the deadline ("The share-lost marker"), or the work-folder lock cannot be opened by the deadline, it closes and returns `LockContentionException` (`Path` is the work folder), processing nothing.
When cancelled while waiting, it also closes, processes nothing, and returns `OperationCanceledException`.
The work-folder lock is held until all journals are processed, and its `.lock` is not deleted when closed.
If `.txfio/` does not exist, it returns `NoPendingTransactions` without opening the work-folder lock.
Stale means that the journal's liveness lock `.txfio/tx-{guid}.lock` can be opened with `FileMode.OpenOrCreate`, `FileShare.None`, `FileOptions.DeleteOnClose`.
When the file does not exist (a crashed transaction, or a journal left by a version before liveness locks), it can be created and opened, so it is stale.
On a sharing violation, the owner is alive (in the same process or another), so the journal is skipped: not read, not deleted, and neither its `.txnew` files nor its `CreateDirectory` directories are touched.
Failures other than a sharing violation are returned as that exception.
The handle that was opened is held until that journal is processed, and closed after the journal is deleted.
Even when an operation matches neither Before nor After during roll-forward, the remaining operations continue to be applied, and the journal is deleted after the `.txnew` of the operations that were not applied are deleted.
The result reports `ConflictDetected` only once, and the next `RecoverAsync` does not process that journal again.
If the journal is gone after opening, the owner finished normally, so it closes without doing anything and does not count it in the result.
Even if `RecoverAsync` runs concurrently on the same work folder, one side skips with a sharing violation, so the same journal is never processed twice.
The library tells roll-forward from rollback by the presence of the `Committing` marker, runs it, and returns the result.
To avoid files changing at unexpected times, nothing runs until the application calls it.
On the other hand, once it is called, the roll-forward or rollback decision is uniquely determined by data consistency, so the library makes that decision automatically.
Like the write APIs, it is an async API.

### Unreadable journals

A journal whose liveness lock could be opened is read; when it cannot be interpreted as JSON (`JsonException`, or deserialization returns null), it is unreadable.
Even when it can be read as JSON, it is unreadable when:
`version` is not 1;
an operation kind is not a name in `PendingChangeKind` (numeric kinds are not accepted either);
an operation's `path` is empty, or a Move's `newPath` is empty.
A journal whose `version` is not 1 is decided before interpreting the operations; it may have been left by a newer version of the library, so the `.txnew` and `.prev` cleanup below does not run either, nothing is touched, and it is `JournalUnreadable`.
The journal is not deleted.
Directories created by `CreateDirectory` cannot be identified, because the document cannot be read, so they stay.
Files whose names end in `.{guid}.txnew`, and those with `.prev` appended, are deleted from under the work folder (excluding `.txfio`).
The operations are unknown, so only here is the work folder walked.
Unreadable folders and reparse points (junctions, symbolic links) are skipped and not followed.
The comparison ignores case.
`guid` is taken from the journal's file name.
Other readable journals are processed normally.
If even one journal is unreadable, the return value gives priority to `JournalUnreadable`.
The journal stays, so the next `BeginAsync` and `CommitAsync` stay `RecoveryRequiredException`.
It is not resolved until someone fixes or deletes the journal.
`RecoverAsync` after the fix rolls forward or back with what it could read.
After it is deleted, the remaining `CreateDirectory` directories stay as untracked children.
When reading throws `IOException`, that exception is rethrown.
Neither the journal nor the `.txnew` files are deleted.
No `RecoverReport` is returned.
Journals processed earlier in lexical order are not undone.
The remaining journals are not processed.
The work-folder lock is closed.

### Invariant on journal existence

`.txfio/tx-{guid}.journal` not existing means the transaction has been committed (or never started).
Even if a crash happens after the journal is deleted and before the locks are released, the file locks the OS held are released automatically, so it is safe (it can be treated as outside Recover's scope)

### The `RecoverResult` type

`RecoverResult` is an enum separate from `CommitResult`.
`RecoverAsync()` returns `RecoverReport`, and this enum value is in `Result` ("Result details").
The values are `NoPendingTransactions` = 0, `RolledBack` = 1, `RolledForward` = 2, `ConflictDetected` = 3, `JournalUnreadable` = 4.
There are no gaps.
`ConflictDetected` means that external interference caused an operation that matches neither Before nor After, and that journal is gone.
`JournalUnreadable` means that at least one journal was unreadable.
That journal remains.
When several results apply, only one is returned, in the order `JournalUnreadable`, `ConflictDetected`, `RolledForward`, `RolledBack`.
`CommitAsync` returns a result while the process is alive, and `RecoverAsync()` returns a result from another process at the next start; their meanings differ, so the types are separate.
Journals of live transactions are not stale, so they are not counted.
If only skipped journals exist, `NoPendingTransactions` is returned.
No value for "skipped" is added

### Result details

The enum values of `CommitResult` and `RecoverResult` stay.
`CommitAsync` returns `CommitReport`, and `RecoverAsync` returns `RecoverReport`.
Since the library is not published yet, there is no compatibility with the earlier shape that returned the enum values directly.

`CommitReport` has `Result` (`CommitResult`) and `Operations` (a list of `OperationReport`).
When `Succeeded`, `Operations` is empty.
`Failed` lists only the operations rejected at the check, and `PartialConflict` only the operations skipped during apply, in apply order.
Operations that were applied are not listed.

`OperationReport` has the path, the `Move` destination (null otherwise), the kind `PendingChangeKind`, the disposition, and the reason.
The disposition is `Rejected` (rejected at the check) or `Skipped` (skipped during apply).

The reason `OperationFailureReason` is one of the following. There are no gaps.

- `Missing` = 0. The target does not exist
- `AlreadyExists` = 1. It already exists
- `ReplacedByFile` = 2. It was swapped for a file or a directory
- `DirectoryPreconditions` = 3. The direct-children conditions of a directory are not met
- `BeforeAfterMismatch` = 4. It matches neither Before nor After
- `SharingViolation` = 5. A sharing violation
- `IoFailure` = 6. Another IO failure (including `UnauthorizedAccessException`)
- `ExternalChange` = 7. When `detectExternalChanges` is true, the size or last write time of the recorded real file differs
- `ReadOnly` = 8. The Update target, or the target of a file Delete, has the read-only attribute

The check uses `Missing`, `AlreadyExists`, `ReplacedByFile`, `DirectoryPreconditions`, `ExternalChange`, and `ReadOnly`.
When `.txnew` cannot be read as a file, the check also uses `IoFailure`.
Apply uses `BeforeAfterMismatch`, `SharingViolation`, and `IoFailure`.
During apply, `AlreadyExists` when the destination already exists, `Missing` when neither the source nor the destination exists, and `ReplacedByFile` when a file and a directory were swapped.
`IoFailure` when `.txnew` is gone and only Before matches.
`UnauthorizedAccessException` is `IoFailure`.

`Failed` does not touch anything on disk (except directories `CreateDirectory` has already created; discard after a failure is the same as Dispose of an uncommitted transaction).
The journal stays, and the transaction is not committed.

In the same transaction, `CommitAsync` can be called again after fixing the state.
`PartialConflict` finishes by deleting the journal and the `.txnew` of operations that were not applied.
The same instance cannot retry.
A path skipped because of a sharing violation can be retried in a new transaction.
With `BeforeAfterMismatch`, repeating the same write does not restore the intended state.
The library does not retry sharing violations automatically.

`RecoverReport` has `Result` (the `RecoverResult` by the current priority) and `Journals` (a list of `JournalReport`).
They are listed in lexical order of the path, ignoring case.
`JournalReport` has the transaction ID, that journal's `RecoverResult`, and the list of operations skipped because of conflicts.
The operation list is empty for journals without conflicts and for unreadable journals.
An unreadable journal whose transaction ID cannot be taken from its file name makes the whole result `JournalUnreadable`, and is not listed.
Live journals are not listed.
Details of `ConflictDetected` are in this return value, and the journal is deleted as before.
The next `RecoverAsync` does not process that journal again.
When reading throws `IOException`, the exception is rethrown as before, and no `RecoverReport` is returned.

## Scope and what is not supported

### Supported operations

Create/Update/Delete/DeleteTree/Rename/Move of files and directories, Copy inside the work folder, directory Import / Export, `CreateDirectory`, ZIP archive create / Export / extract / Import, appending text, and listing direct children in the post-commit view.
`ReadAsync` of a directory, and archive formats other than ZIP (tar, bare GZip, Brotli), are not supported

### Out of scope (explicitly)

There are no operations that set attributes (read-only, hidden, and so on), times, or ACLs inside a transaction.
Setting them would add operation kinds and require metadata in the Before / After checks, while setting them with the plain file API after commit is enough.
Update keeps the ACL, attributes, and creation time of the replaced file ("Write model").
There is no progress by commit count (`IProgress` on `CommitAsync`) either (kept on the roadmap)

### Move constraints

Only moves within the same volume are supported.
A move to another volume (another drive, another file server share) is an error.
A rename across volumes is not guaranteed to be atomic by the OS, and becomes heavy work like copy + delete.
If the library ran this heavy work implicitly, users would run expensive operations without noticing, so to cross volumes they use explicit `ImportAsync`/`ExportAsync`.
A mount point inside the work folder at call time is a reparse point, and throws `InvalidOperationException`.
When commit and recovery apply a move, they rename without the flag that allows a copy.
If that rename crosses volumes, it does not become copy + delete; it is an IO failure of apply

### Target platform

Start as Windows only (NTFS and SMB file servers).
Guarantees of Move/Rename atomicity and lock behavior on .NET differ a lot between operating systems and file systems, so the scope is narrowed first to settle the design.
Cross-platform support is left as future work.
The crash safety Txfio guarantees is based on the file API and Flush semantics on Windows/NTFS; the internal durability guarantees of an SMB file server (how the server cache is handled, and so on) are outside Txfio's responsibility

## Implementation policy and project layout

### Library name

`Txfio`.
`TxFs` was considered first, but NuGet already has a package `Txfs` with the same name (package IDs are case-insensitive, so it is technically identical), last updated in August 2019, unmaintained for more than six years, with a similar description, so the name was changed.
No existing NuGet package matches `Txfio` exactly (the similar `EQXMedia.TxFileSystem` has a different name)

### Project layout

Split into several projects instead of one (the core library, test projects, and so on). Folders and projects are split by responsibility.

### Test strategy

In addition to normal unit tests, there are tests that stop a commit on purpose partway.
Checkpoints that only tests can set stop right after `Committing` is written (`AfterCommitting`), and right after each operation succeeds in apply order (`AfterApply`).
The Dispose after the stop does not roll back, and the tests verify that `RecoverAsync` on the same work folder can recover the real files.
`Applied` is not written

### Target framework

A single target, `net8.0`.
Old environments such as .NET Standard 2.0 are not supported.
A `net8.0` package can be referenced from net8 / net9 / net10 applications.
The run-time guarantee is Windows (NTFS/SMB) only.
The development SDK may be .NET 10.
Not `net8.0-windows` (it could not be referenced from the Linux SDK).
This fits a design built on `IAsyncDisposable` (async APIs, the `await using` pattern).

### Abstracting file system access

The library's implementation does not put an abstraction layer such as `System.IO.Abstractions` in between; it uses `System.IO` directly,
because the core value of this library (atomicity of rename and copy, and actual behavior over network file systems) cannot be verified by mocking behind an abstraction layer.
The public API is an interface (`ITransaction`).
Text and JSON reads and writes are its methods too, so callers can mock it without downcasting to a concrete type.
Txfio's own implementation tests run against real files, and tests of code that uses Txfio mock the interface.

### NuGet package layout

A single `Txfio` package.
There is no split into a testing helper package (such as `Txfio.Testing`).
Publishing to nuget.org happens after the GitHub repository is made public (after Phase 3 is done).

### Namespace

A flat `Txfio`. No prefix with a personal or company name.

### API naming

The public type for a transaction is `ITransaction` (it is meant to be used as `Txfio.ITransaction` with the `Txfio` namespace, so the type name does not repeat a qualifier such as `WorkFolder`).
The entry point is static methods on `Txfio`, with no dedicated class such as `Factory`:

```csharp
await using var tx = await Txfio.BeginAsync(path);
```
