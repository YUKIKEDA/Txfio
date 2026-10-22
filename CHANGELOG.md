# Changelog

All notable changes to Txfio are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). A 0.x minor
version may break the public API.

## [0.3.0]

Clients that share a work folder now need the same on-disk protocol
version, not the same library version, and a client of another protocol
version is detected instead of corrupting the work folder.

### Added

- **`IncompatibleClientException`.** Each client holds
  `.txfio/proto-{N}.lock` (protocol 1 in this release) together with the
  work-folder lock. When a client of another protocol version is using the
  work folder, the write operations and `RecoverAsync` wait within their
  lock wait, then throw `IncompatibleClientException` without touching the
  work folder. `BeginAsync`, `ReadAsync`, `ExistsAsync`, `ExportAsync`, and
  `ExportArchiveAsync` do not check. Catch it where you catch
  `LockContentionException`, and update the other clients. Detection
  relies on Windows file sharing modes, including over SMB, and detects
  nothing on Linux.

  ```csharp
  using Txfio;

  try
  {
      await using ITransaction tx = await TxFileIO.BeginAsync(@"D:\share\work");
      await tx.WriteAllTextAsync("a.txt", "a");
      await tx.CommitAsync();
  }
  catch (IncompatibleClientException)
  {
      // A client of another protocol version is using the folder.
  }
  ```

- **Journals record their protocol version.** The first line has
  `"protocol":1`. `RecoverAsync` reports a journal of another protocol
  version as `JournalUnreadable` and does not touch it, and staging and
  commit throw `RecoveryRequiredException` while it remains. A journal
  without the field, as 0.2.0 writes it, is read as protocol 1.

### Changed

- **0.2.0 clients can share a work folder with this version.** They take
  no protocol lock, so they cannot be detected, but they follow the rest of
  protocol 1. They must not share a work folder with a later protocol
  version.
- **Upgrade after recovering.** Before you upgrade the clients of a work
  folder, call `RecoverAsync` with the old version, so no crashed journal
  of the old protocol is left. A later protocol reads only the journals of
  the protocol just before it.

## [0.2.0] - 2026-10-21

A crashed transaction no longer blocks the whole work folder, and the names
of staging entries are now part of the contract.

### Breaking

- **The static entry class `Txfio` is now `TxFileIO`.** The class had
  the same name as its namespace, so after `using Txfio;` the documented
  call `Txfio.BeginAsync(path)` did not compile (CS0234) and needed
  `Txfio.Txfio.BeginAsync(path)`. Replace `Txfio.BeginAsync`,
  `Txfio.RecoverAsync`, and `Txfio.Txfio.…` with `TxFileIO.BeginAsync`
  and `TxFileIO.RecoverAsync`. The parameters and results are the same.

  ```csharp
  using Txfio;

  await TxFileIO.RecoverAsync(@"D:\share\work");
  await using ITransaction tx = await TxFileIO.BeginAsync(@"D:\share\work");
  ```

- **Do not share a work folder between 0.1.0 and 0.2.0 clients.** The two
  lock the work folder in incompatible ways. Upgrade every process that
  opens the folder together. A `share-lost.lock` left by 0.1.0 is neither
  used nor deleted.
- **`BeginAsync` no longer throws `RecoveryRequiredException`.** Staging
  methods and `CommitAsync` throw it only for paths that recovering a
  crashed transaction may change. Its `Path` is the overlapping path (the
  work folder when the journal cannot be read). Code that caught it around
  `BeginAsync` should catch it around staging and commit instead; calling
  `RecoverAsync` first when you open the folder stays the recommended
  pattern.
- **`RecoverAsync` runs while other transactions are alive.** It holds the
  work folder as shared and recovers each crashed journal while holding the
  locks on its paths. It no longer throws `LockContentionException` because
  a transaction is alive. Journals whose paths another transaction still
  holds are reported as the new `RecoverResult.Contended` and left for a
  later call; handle that value where you switch on `RecoverReport.Result`.
- **`CreateDirectoryAsync` reserves its directory until the transaction
  ends.** Other transactions get `LockContentionException` when they stage
  under it, instead of only during the call.
- **Staging entry names are reserved.** `{name}.{txid}.txnew`,
  `{name}.{txid}.txnew.prev`, and `{name}.{txid}.txold` (with `{txid}` in
  `Guid.ToString("D")` form) are part of the contract. A path with such a
  segment throws `InvalidOperationException` in every API, also through an
  8.3 short name or a trailing dot. `ImportAsync` rejects a source, or an
  entry under it, with such a name before staging. Rename such files
  outside the work folder before importing them.
- **Walks skip the staging entries of every transaction.**
  `GetEntriesAsync`, a directory `CopyAsync`, `ExportAsync`, and the input
  of `CreateArchiveAsync` no longer list or copy the `.txnew`, `.txnew.prev`,
  or `.txold` of other transactions, or ones left by a crash.
- **A directory `DeleteAsync` conflict names the blocking child.** When it
  fails at staging because of an unscheduled direct child,
  `ExternalConflictException.Path` is that child (the first in ordinal order
  of the upper-cased paths), not the directory. The commit-time
  `DirectoryPreconditions` report still names the directory.

### Added

- `TxFileIO.IsStagingName(string name)` returns whether a single file or
  directory name is a staging name. Use it to skip staging entries when you
  list the work folder with the plain file API.

### Changed

- Extract and archive entry names are rejected only when a segment is a
  staging name. A name that merely ends in `.txnew`, such as `notes.txnew`,
  is now allowed.

## [0.1.0] - 2026-09-27

First release of transactional file IO for Windows (NTFS and SMB).

[0.3.0]: https://github.com/YUKIKEDA/Txfio/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/YUKIKEDA/Txfio/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/YUKIKEDA/Txfio/releases/tag/v0.1.0
