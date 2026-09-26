using System.Security.Cryptography;
using System.Text;

namespace Txfio;

/// <summary>
/// トランザクションが押さえたパスのロックを持つ
/// </summary>
internal sealed class PathLockSet
{
    private const int SharingViolation = 32;

    private const int LockViolation = 33;

    // Linux の EAGAIN（EWOULDBLOCK）では、.NET は Unix の errno をそのまま HResult に入れる
    private const int LinuxWouldBlock = 11;

    private readonly IFaultInjector _faults;

    private readonly Dictionary<string, FileStream> _handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, FileStream> _intents = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _exclusiveIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ワークフォルダ全体のロック（_workFolderState が Shared か Exclusive のあいだだけ開いている）
    private FileStream? _workFolderHandle;

    private WorkFolderState _workFolderState;

    // パスロックか意図ロックを持ったまま、ワークフォルダ全体のロックを持っていないあいだ開く
    private FileStream? _shareLost;

    /// <summary>
    /// 失敗も途中停止もしないロックの集合を作る
    /// </summary>
    internal PathLockSet()
        : this(NoFaultInjector.Instance)
    {
    }

    /// <summary>
    /// 渡した失敗と途中停止を使うロックの集合を作る
    /// </summary>
    /// <param name="faults">この集合の失敗と途中停止</param>
    internal PathLockSet(IFaultInjector faults)
    {
        _faults = faults;
    }

    /// <summary>
    /// ワークフォルダ全体のロックの状態
    /// </summary>
    private enum WorkFolderState
    {
        /// <summary>
        /// 持っていない
        /// </summary>
        None,

        /// <summary>
        /// 共有で持っている
        /// </summary>
        Shared,

        /// <summary>
        /// 排他で持っている
        /// </summary>
        Exclusive,

        /// <summary>
        /// 共有へ戻せなかった（次の取得で開き直す）
        /// </summary>
        Lost,
    }

    /// <summary>
    /// ロックファイルの絶対パスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPath">正規化した絶対パス</param>
    /// <returns>`.lock` ファイルのパス</returns>
    internal static string FilePath(string workFolder, string fullPath)
    {
        return LockFile(workFolder, RelativeKey(workFolder, fullPath));
    }

    /// <summary>
    /// 意図ロックの絶対パスを返す（相対パスの末尾に `\*` を足してからハッシュする）
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPath">正規化した絶対パス</param>
    /// <returns>意図ロックの `.lock` ファイルのパス</returns>
    internal static string IntentFilePath(string workFolder, string fullPath)
    {
        return LockFile(workFolder, RelativeKey(workFolder, fullPath) + @"\*");
    }

    /// <summary>
    /// 他のハンドルが開いているために失敗したかを返す
    /// </summary>
    /// <param name="exception">オープンで起きた例外</param>
    /// <returns>共有違反またはロック違反なら <see langword="true"/></returns>
    internal static bool IsSharingViolation(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        if (code == SharingViolation || code == LockViolation)
        {
            return true;
        }

        // Linux の判定は開発環境でテストを回すためのもので、実行時に保証するのは Windows だけである
        return OperatingSystem.IsLinux() && exception.HResult == LinuxWouldBlock;
    }

    /// <summary>
    /// ワークフォルダ全体のロックを共有で開く（既に持っていれば開き直さず、失っていれば開き直す）
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <exception cref="LockContentionException">期限までにワークフォルダ全体のロックを取れない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    /// <returns>取れたこと（取れなければ例外）</returns>
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
    /// ワークフォルダ全体のロックを排他で開く（共有を持っていれば閉じて取り直す）
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <exception cref="LockContentionException">期限までにワークフォルダ全体のロックを取れない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    /// <exception cref="IOException">共有へ戻すときの、共有違反以外の失敗</exception>
    /// <returns>取れたこと（取れなければ例外）</returns>
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
    /// しるし（`.txfio/share-lost.lock`）が使用中なら空くまで待つ（空かなければ排他をやめて共有に戻す）
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <exception cref="LockContentionException">期限までにしるしが空かない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    /// <returns>しるしが空いていること（空かなければ例外）</returns>
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
    /// パスをロックし、読んでいるあいだ守るディレクトリの意図ロックを排他で取る（破棄すると、この呼び出しで取った排他の意図ロックを閉じる）
    /// </summary>
    /// <remarks>
    /// ディレクトリのコピー元と ZIP の入力を読むあいだ、他のトランザクションが配下をステージしたりコミットしたりしないようにする
    /// 既に排他で持っていた意図ロックは閉じない
    /// </remarks>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPaths">ロックする正規化した絶対パス</param>
    /// <param name="reservedPaths">トランザクションの終わりまで意図ロックを排他で持つディレクトリ</param>
    /// <param name="readDirectories">呼び出しのあいだだけ意図ロックを排他で持つディレクトリ</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <returns>読み終えたら破棄する範囲</returns>
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
    /// 絶対パスを大文字化して辞書順に並べ、祖先の意図ロックを取ってからその順でロックし、同じパスは開き直さない
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPaths">正規化した絶対パス</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <returns>取れたこと（取れなければ例外）</returns>
    internal Task AcquireAsync(string workFolder, string[] fullPaths, LockAttempt attempt)
    {
        return AcquireCoreAsync(workFolder, fullPaths, exclusiveIntentPaths: null, attempt);
    }

    /// <summary>
    /// パスロックに加え、予約するディレクトリの意図ロックを排他で取る
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPaths">正規化した絶対パス</param>
    /// <param name="exclusiveIntentPaths">排他の意図ロックを取るディレクトリ</param>
    /// <param name="attempt">この呼び出しのロック待ち</param>
    /// <returns>取れたこと（取れなければ例外）</returns>
    internal Task AcquireReservingAsync(
        string workFolder,
        string[] fullPaths,
        string[] exclusiveIntentPaths,
        LockAttempt attempt)
    {
        return AcquireCoreAsync(workFolder, fullPaths, exclusiveIntentPaths, attempt);
    }

    /// <summary>
    /// 持っているハンドルを閉じる（ロックファイルは残す）
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
        return new LockContentionException("他のトランザクションがこのパスを使用中です: " + workFolder, workFolder);
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
            throw new LockContentionException("他のトランザクションがこのパスを使用中です: " + fullPath, fullPath);
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
            throw new LockContentionException("他のトランザクションがこのパスを使用中です: " + directory, directory);
        }
    }

    // 排他の意図ロックを待っているあいだはワークフォルダ全体のロックを持たない
    // 持ったままだと、待っている側が排他に上げるのを塞ぐ
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
                            "他のトランザクションがこのパスを使用中です: " + directory,
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
            // 共有へ戻せなくても、呼び出し側が元の例外を返す
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
    /// 読んでいるあいだだけ排他で持つ意図ロックを、破棄するときに閉じる
    /// </summary>
    internal readonly struct ReadingScope : IDisposable
    {
        private readonly PathLockSet _locks;

        private readonly IReadOnlyList<string> _directories;

        /// <summary>
        /// 閉じる対象を覚える
        /// </summary>
        /// <param name="locks">ロックの集合</param>
        /// <param name="directories">この呼び出しで排他にした意図ロックのディレクトリ</param>
        internal ReadingScope(PathLockSet locks, IReadOnlyList<string> directories)
        {
            _locks = locks;
            _directories = directories;
        }

        /// <summary>
        /// この呼び出しで排他にした意図ロックを閉じる
        /// </summary>
        public void Dispose()
        {
            _locks?.ReleaseIntents(_directories);
        }
    }
}
