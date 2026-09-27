using System.Security.Cryptography;
using System.Text;

namespace Txfio;

/// <summary>
/// Holds the locks on the paths a transaction has taken.
/// </summary>
internal sealed class PathLockSet
{
    private const int SharingViolation = 32;

    private const int LockViolation = 33;

    // For Linux EAGAIN (EWOULDBLOCK), .NET puts the Unix errno into HResult as is.
    private const int LinuxWouldBlock = 11;

    private readonly IFaultInjector _faults;

    private readonly Dictionary<string, FileStream> _handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, FileStream> _intents = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _exclusiveIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // The work-folder lock (open only while _workFolderState is Shared or Exclusive).
    private FileStream? _workFolderHandle;

    private WorkFolderState _workFolderState;

    // Open while a path lock or intent lock is held without the work-folder lock.
    private FileStream? _shareLost;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathLockSet"/> class that neither fails nor stops partway.
    /// </summary>
    internal PathLockSet()
        : this(NoFaultInjector.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PathLockSet"/> class that uses the given failures and partial stops.
    /// </summary>
    /// <param name="faults">The failures and partial stops of this set.</param>
    internal PathLockSet(IFaultInjector faults)
    {
        _faults = faults;
    }

    /// <summary>
    /// The state of the work-folder lock.
    /// </summary>
    private enum WorkFolderState
    {
        /// <summary>
        /// Not held.
        /// </summary>
        None,

        /// <summary>
        /// Held as shared.
        /// </summary>
        Shared,

        /// <summary>
        /// Held exclusively.
        /// </summary>
        Exclusive,

        /// <summary>
        /// Could not go back to shared (reopen at the next acquisition).
        /// </summary>
        Lost,
    }

    /// <summary>
    /// Returns the absolute path of a lock file.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPath">The normalized absolute path.</param>
    /// <returns>The path of the <c>.lock</c> file.</returns>
    internal static string FilePath(string workFolder, string fullPath)
    {
        return LockFile(workFolder, RelativeKey(workFolder, fullPath));
    }

    /// <summary>
    /// Returns the absolute path of an intent lock (the relative path is hashed after appending <c>\*</c>).
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPath">The normalized absolute path.</param>
    /// <returns>The path of the intent lock's <c>.lock</c> file.</returns>
    internal static string IntentFilePath(string workFolder, string fullPath)
    {
        return LockFile(workFolder, RelativeKey(workFolder, fullPath) + @"\*");
    }

    /// <summary>
    /// Returns whether an open failed because another handle has the file open.
    /// </summary>
    /// <param name="exception">The exception from the open.</param>
    /// <returns><see langword="true"/> for a sharing violation or a lock violation.</returns>
    internal static bool IsSharingViolation(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        if (code == SharingViolation || code == LockViolation)
        {
            return true;
        }

        // The Linux check is for running the tests in development; only Windows is guaranteed at run time.
        return OperatingSystem.IsLinux() && exception.HResult == LinuxWouldBlock;
    }

    /// <summary>
    /// Opens the work-folder lock as shared (does not reopen if already held; reopens if lost).
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <exception cref="LockContentionException">The work-folder lock cannot be taken by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <returns><see langword="true"/> when taken (otherwise an exception is thrown).</returns>
    internal async Task AcquireSharedAsync(string workFolder, LockAttempt attempt)
    {
        if (_workFolderState == WorkFolderState.Lost)
        {
            await RestoreSharedAsync(workFolder, attempt).ConfigureAwait(false);
            return;
        }

        if (_workFolderState != WorkFolderState.None)
        {
            return;
        }

        try
        {
            _workFolderHandle = await OpenWaitingAsync(FilePath(workFolder, workFolder), FileShare.ReadWrite, attempt)
                .ConfigureAwait(false);
            _workFolderState = WorkFolderState.Shared;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw Contention(workFolder);
        }
    }

    /// <summary>
    /// Opens the work-folder lock exclusively (if shared is held, closes it and takes it again).
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <exception cref="LockContentionException">The work-folder lock cannot be taken by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="IOException">A failure other than a sharing violation while going back to shared.</exception>
    /// <returns><see langword="true"/> when taken (otherwise an exception is thrown).</returns>
    internal async Task AcquireExclusiveAsync(string workFolder, LockAttempt attempt)
    {
        if (_workFolderState == WorkFolderState.Lost)
        {
            await RestoreSharedAsync(workFolder, attempt).ConfigureAwait(false);
        }

        if (_workFolderState == WorkFolderState.Exclusive)
        {
            return;
        }

        bool restoreSharedOnFailure = _workFolderState == WorkFolderState.Shared;
        if (restoreSharedOnFailure)
        {
            HoldShareLostBeforeDrop(workFolder);
            ReleaseWorkFolder();
        }

        try
        {
            _workFolderHandle = await OpenWaitingAsync(FilePath(workFolder, workFolder), FileShare.None, attempt)
                .ConfigureAwait(false);
            _workFolderState = WorkFolderState.Exclusive;
            CloseShareLost();
        }
        catch (OperationCanceledException)
        {
            if (restoreSharedOnFailure)
            {
                RestoreAfterCancel(workFolder);
            }

            throw;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            if (restoreSharedOnFailure)
            {
                await RestoreOrMarkLostAsync(workFolder, attempt).ConfigureAwait(false);
            }

            throw Contention(workFolder);
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            if (restoreSharedOnFailure)
            {
                await RestoreOrMarkLostAsync(workFolder, attempt).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Waits until the share-lost marker (<c>.txfio/share-lost.lock</c>) is free if it is in use (if it does not become free, gives up the exclusive lock and goes back to shared).
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <exception cref="LockContentionException">The marker does not become free by the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <returns>A task that completes when the marker is free (otherwise an exception is thrown).</returns>
    internal async Task RejectForeignLocksAsync(string workFolder, LockAttempt attempt)
    {
        try
        {
            while (ShareLostBusy(workFolder))
            {
                if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
                {
                    HoldShareLostBeforeDrop(workFolder);
                    ReleaseWorkFolder();
                    await RestoreOrMarkLostAsync(workFolder, attempt).ConfigureAwait(false);
                    throw Contention(workFolder);
                }
            }
        }
        catch (OperationCanceledException)
        {
            HoldShareLostBeforeDrop(workFolder);
            ReleaseWorkFolder();
            RestoreAfterCancel(workFolder);
            throw;
        }
    }

    /// <summary>
    /// Locks the paths, and takes exclusive intent locks on the directories to protect while reading (disposing closes the exclusive intent locks taken by this call).
    /// </summary>
    /// <remarks>
    /// Keeps other transactions from staging or committing under a directory copy source or a ZIP input while it is read.
    /// Intent locks that were already held exclusively are not closed.
    /// </remarks>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPaths">The normalized absolute paths to lock.</param>
    /// <param name="reservedPaths">The directories whose intent locks are held exclusively until the transaction ends.</param>
    /// <param name="readDirectories">The directories whose intent locks are held exclusively only during the call.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <returns>The scope to dispose after reading.</returns>
    internal async Task<ReadingScope> AcquireForReadingAsync(
        string workFolder,
        string[] fullPaths,
        string[] reservedPaths,
        string[] readDirectories,
        LockAttempt attempt)
    {
        List<string> scoped = new List<string>();
        foreach (string directory in readDirectories)
        {
            if (!_exclusiveIntents.Contains(directory))
            {
                scoped.Add(directory);
            }
        }

        try
        {
            await AcquireCoreAsync(workFolder, fullPaths, reservedPaths.Concat(readDirectories).ToArray(), attempt)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseIntents(scoped);
            throw;
        }

        return new ReadingScope(this, scoped);
    }

    /// <summary>
    /// Sorts the upper-cased absolute paths lexically, takes the ancestors' intent locks, then locks the paths in that order, without reopening the same path.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPaths">The normalized absolute paths.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <returns><see langword="true"/> when taken (otherwise an exception is thrown).</returns>
    internal Task AcquireAsync(string workFolder, string[] fullPaths, LockAttempt attempt)
    {
        return AcquireCoreAsync(workFolder, fullPaths, exclusiveIntentPaths: null, attempt);
    }

    /// <summary>
    /// In addition to the path locks, takes exclusive intent locks on the directories being reserved.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPaths">The normalized absolute paths.</param>
    /// <param name="exclusiveIntentPaths">The directories to take exclusive intent locks on.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <returns><see langword="true"/> when taken (otherwise an exception is thrown).</returns>
    internal Task AcquireReservingAsync(
        string workFolder,
        string[] fullPaths,
        string[] exclusiveIntentPaths,
        LockAttempt attempt)
    {
        return AcquireCoreAsync(workFolder, fullPaths, exclusiveIntentPaths, attempt);
    }

    /// <summary>
    /// Closes the handles held (the lock files stay).
    /// </summary>
    internal void Release()
    {
        foreach (FileStream handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
        foreach (FileStream intent in _intents.Values)
        {
            intent.Dispose();
        }

        _intents.Clear();
        _exclusiveIntents.Clear();
        ReleaseWorkFolder();
        CloseShareLost();
    }

    private static string RelativeKey(string workFolder, string fullPath)
    {
        string relative = System.IO.Path.GetRelativePath(workFolder, fullPath);
        return relative.Replace(
            System.IO.Path.AltDirectorySeparatorChar,
            System.IO.Path.DirectorySeparatorChar).ToUpperInvariant();
    }

    private static string LockFile(string workFolder, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return System.IO.Path.Combine(MetadataNames.LockFolderPath(workFolder), hex + ".lock");
    }

    private static List<string> Ancestors(string workFolder, string fullPath)
    {
        string root = System.IO.Path.TrimEndingDirectorySeparator(workFolder);
        string prefix = root + System.IO.Path.DirectorySeparatorChar;
        List<string> ancestors = new List<string>();
        string? current = System.IO.Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(current);
            if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            ancestors.Add(trimmed);
            current = System.IO.Path.GetDirectoryName(trimmed);
        }

        ancestors.Reverse();
        return ancestors;
    }

    private static List<string> Order(string[] fullPaths)
    {
        List<string> unique = new List<string>();
        foreach (string fullPath in fullPaths)
        {
            bool seen = false;
            foreach (string existing in unique)
            {
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                unique.Add(fullPath);
            }
        }

        unique.Sort(static (left, right) => string.Compare(
            left.ToUpperInvariant(),
            right.ToUpperInvariant(),
            StringComparison.Ordinal));
        return unique;
    }

    private static LockContentionException Contention(string workFolder)
    {
        return new LockContentionException("Another transaction is using this path: " + workFolder, workFolder);
    }

    private FileStream OpenLockFile(string lockPath, FileShare share)
    {
        _faults.ThrowIfOpenArmed(share);
        string? directory = System.IO.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            share);
    }

    private async Task AcquireOneAsync(string workFolder, string fullPath, LockAttempt attempt)
    {
        if (_handles.ContainsKey(fullPath))
        {
            return;
        }

        try
        {
            FileStream handle = await OpenWaitingAsync(FilePath(workFolder, fullPath), FileShare.None, attempt)
                .ConfigureAwait(false);
            _handles.Add(fullPath, handle);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new LockContentionException("Another transaction is using this path: " + fullPath, fullPath);
        }
    }

    private void ReleaseWorkFolder()
    {
        _workFolderHandle?.Dispose();
        _workFolderHandle = null;
        _workFolderState = WorkFolderState.None;
    }

    private async Task RestoreSharedAsync(string workFolder, LockAttempt attempt)
    {
        while (true)
        {
            try
            {
                RestoreSharedOnce(workFolder);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
                {
                    throw Contention(workFolder);
                }
            }
        }
    }

    private void RestoreSharedOnce(string workFolder)
    {
        if (_workFolderHandle is null)
        {
            _workFolderHandle = OpenLockFile(FilePath(workFolder, workFolder), FileShare.ReadWrite);
            _workFolderState = WorkFolderState.Shared;
        }

        CloseShareLost();
    }

    private async Task<FileStream> OpenWaitingAsync(string lockPath, FileShare share, LockAttempt attempt)
    {
        while (true)
        {
            try
            {
                return OpenLockFile(lockPath, share);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    private bool ShareLostBusy(string workFolder)
    {
        string path = MetadataNames.ShareLostLockPath(workFolder);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return true;
        }

        return false;
    }

    private void HoldShareLostBeforeDrop(string workFolder)
    {
        if (_shareLost is not null || !HoldsPathOrIntent())
        {
            return;
        }

        Directory.CreateDirectory(MetadataNames.FolderPath(workFolder));
        _shareLost = new FileStream(
            MetadataNames.ShareLostLockPath(workFolder),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 1);
    }

    private bool HoldsPathOrIntent()
    {
        return _intents.Count > 0 || _handles.Count > 0;
    }

    private void CloseShareLost()
    {
        if (_shareLost is null)
        {
            return;
        }

        _shareLost.Dispose();
        _shareLost = null;
    }

    private void RestoreAfterCancel(string workFolder)
    {
        try
        {
            RestoreSharedOnce(workFolder);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            _workFolderState = WorkFolderState.Lost;
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            _workFolderState = WorkFolderState.Lost;
            throw;
        }
    }

    private async Task RestoreOrMarkLostAsync(string workFolder, LockAttempt attempt)
    {
        try
        {
            await RestoreSharedAsync(workFolder, attempt).ConfigureAwait(false);
        }
        catch (LockContentionException)
        {
            _workFolderState = WorkFolderState.Lost;
            throw;
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            _workFolderState = WorkFolderState.Lost;
            throw;
        }
    }

    private async Task AcquireCoreAsync(
        string workFolder,
        string[] fullPaths,
        IReadOnlyCollection<string>? exclusiveIntentPaths,
        LockAttempt attempt)
    {
        HashSet<string> exclusive = exclusiveIntentPaths is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(exclusiveIntentPaths, StringComparer.OrdinalIgnoreCase);
        foreach (string fullPath in Order(fullPaths))
        {
            foreach (string ancestor in Ancestors(workFolder, fullPath))
            {
                await AcquireIntentAsync(workFolder, ancestor, exclusive.Contains(ancestor), attempt).ConfigureAwait(false);
            }

            if (exclusive.Contains(fullPath))
            {
                await AcquireIntentAsync(workFolder, fullPath, exclusive: true, attempt).ConfigureAwait(false);
            }

            await AcquireOneAsync(workFolder, fullPath, attempt).ConfigureAwait(false);
        }
    }

    private async Task AcquireIntentAsync(string workFolder, string directory, bool exclusive, LockAttempt attempt)
    {
        if (_intents.ContainsKey(directory) && (!exclusive || _exclusiveIntents.Contains(directory)))
        {
            return;
        }

        if (exclusive)
        {
            await AcquireExclusiveIntentAsync(workFolder, directory, attempt).ConfigureAwait(false);
            return;
        }

        try
        {
            FileStream intent = await OpenWaitingAsync(IntentFilePath(workFolder, directory), FileShare.ReadWrite, attempt)
                .ConfigureAwait(false);
            _intents.Add(directory, intent);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new LockContentionException("Another transaction is using this path: " + directory, directory);
        }
    }

    // Do not hold the work-folder lock while waiting for an exclusive intent lock.
    // Holding it would block the other side from raising to exclusive.
    private async Task AcquireExclusiveIntentAsync(string workFolder, string directory, LockAttempt attempt)
    {
        bool closedOwnShared = false;
        if (_intents.Remove(directory, out FileStream? held))
        {
            held.Dispose();
            _exclusiveIntents.Remove(directory);
            closedOwnShared = true;
        }

        bool releasedShared = false;
        try
        {
            while (true)
            {
                try
                {
                    _intents.Add(directory, OpenLockFile(IntentFilePath(workFolder, directory), FileShare.None));
                    _exclusiveIntents.Add(directory);
                    return;
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                    if (!releasedShared && _workFolderState == WorkFolderState.Shared)
                    {
                        HoldShareLostBeforeDrop(workFolder);
                        ReleaseWorkFolder();
                        releasedShared = true;
                    }

                    if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
                    {
                        throw new LockContentionException(
                            "Another transaction is using this path: " + directory,
                            directory);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (closedOwnShared)
            {
                RestoreSharedIntent(workFolder, directory);
            }

            throw;
        }
        catch (LockContentionException)
        {
            if (closedOwnShared)
            {
                RestoreSharedIntent(workFolder, directory);
            }

            throw;
        }
        finally
        {
            if (releasedShared)
            {
                try
                {
                    await RestoreSharedAsync(workFolder, attempt).ConfigureAwait(false);
                }
                catch (LockContentionException)
                {
                    _workFolderState = WorkFolderState.Lost;
                }
                catch (OperationCanceledException)
                {
                    _workFolderState = WorkFolderState.Lost;
                }
                catch (Exception exception) when (IoErrors.IsIo(exception))
                {
                    _workFolderState = WorkFolderState.Lost;
                    throw;
                }
            }
        }
    }

    private void RestoreSharedIntent(string workFolder, string directory)
    {
        if (_intents.ContainsKey(directory))
        {
            return;
        }

        try
        {
            _intents.Add(directory, OpenLockFile(IntentFilePath(workFolder, directory), FileShare.ReadWrite));
        }
        catch (Exception exception) when (IoErrors.IsIo(exception))
        {
            // Even if it cannot go back to shared, the caller returns the original exception.
        }
    }

    private void ReleaseIntents(IReadOnlyList<string> directories)
    {
        foreach (string directory in directories)
        {
            if (_intents.Remove(directory, out FileStream? handle))
            {
                handle.Dispose();
            }

            _exclusiveIntents.Remove(directory);
        }
    }

    /// <summary>
    /// Closes, on dispose, the intent locks held exclusively only while reading.
    /// </summary>
    internal readonly struct ReadingScope : IDisposable
    {
        private readonly PathLockSet _locks;

        private readonly IReadOnlyList<string> _directories;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReadingScope"/> struct with what to close.
        /// </summary>
        /// <param name="locks">The set of locks.</param>
        /// <param name="directories">The directories whose intent locks this call made exclusive.</param>
        internal ReadingScope(PathLockSet locks, IReadOnlyList<string> directories)
        {
            _locks = locks;
            _directories = directories;
        }

        /// <summary>
        /// Closes the intent locks this call made exclusive.
        /// </summary>
        public void Dispose()
        {
            _locks?.ReleaseIntents(_directories);
        }
    }
}
