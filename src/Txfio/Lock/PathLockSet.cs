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

    // Linux の EAGAIN（EWOULDBLOCK）。.NET は Unix では errno をそのまま HResult に入れる
    private const int LinuxWouldBlock = 11;

    private const int RetryIntervalMilliseconds = 100;

    private static readonly AsyncLocal<Queue<(FileShare Share, Exception Exception)>?> _openFailures =
        new AsyncLocal<Queue<(FileShare Share, Exception Exception)>?>();

    private readonly Dictionary<string, FileStream> _handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private bool _workFolderExclusive;

    // 共有へ戻せなかったとき、次の取得で開き直す
    private bool _workFolderShareLost;

    private bool _waitArmed;

    private bool _waitForever;

    private long _deadlineTick;

    private CancellationToken _waitCancellation;

    /// <summary>
    /// ロックファイルの絶対パスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPath">正規化した絶対パス</param>
    /// <returns>`.lock` ファイルのパス</returns>
    internal static string FilePath(string workFolder, string fullPath)
    {
        string relative = System.IO.Path.GetRelativePath(workFolder, fullPath);
        relative = relative.Replace(
            System.IO.Path.AltDirectorySeparatorChar,
            System.IO.Path.DirectorySeparatorChar);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return System.IO.Path.Combine(MetadataNames.LockFolderPath(workFolder), hex + ".lock");
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
    /// 次に同じ共有モードで開くとき、指定した例外を投げる。テスト用
    /// </summary>
    /// <param name="share">失敗させる共有モード</param>
    /// <param name="exception">投げる例外</param>
    internal static void FailNextOpen(FileShare share, Exception exception)
    {
        Queue<(FileShare Share, Exception Exception)> queue = _openFailures.Value
            ?? new Queue<(FileShare Share, Exception Exception)>();
        queue.Enqueue((share, exception));
        _openFailures.Value = queue;
    }

    /// <summary>
    /// テストが仕込んだオープン失敗を消す
    /// </summary>
    internal static void ClearOpenFailures()
    {
        _openFailures.Value = null;
    }

    /// <summary>
    /// この公開メソッドのロック待ちを始める（期限は呼び出しの開始から）
    /// </summary>
    /// <param name="lockWait">待つ上限（ゼロは待たない、<see cref="Timeout.InfiniteTimeSpan"/> は期限がない）</param>
    /// <param name="cancellationToken">待ちを取り消すトークン</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> がゼロ未満である（<see cref="Timeout.InfiniteTimeSpan"/> は除く）</exception>
    internal void BeginAttempt(TimeSpan lockWait, CancellationToken cancellationToken)
    {
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        _waitCancellation = cancellationToken;
        _waitArmed = true;
        if (lockWait == Timeout.InfiniteTimeSpan)
        {
            _waitForever = true;
            return;
        }

        _waitForever = false;
        if (lockWait <= TimeSpan.Zero)
        {
            _deadlineTick = Environment.TickCount64;
            return;
        }

        long milliseconds = (long)lockWait.TotalMilliseconds;
        long now = Environment.TickCount64;
        _deadlineTick = milliseconds > long.MaxValue - now ? long.MaxValue : now + milliseconds;
    }

    /// <summary>
    /// ワークフォルダ全体のロックを共有で開く（既に持っていれば開き直さず、失っていれば開き直す）
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <exception cref="LockContentionException">期限までにワークフォルダ全体のロックを取れない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    internal void AcquireShared(string workFolder)
    {
        if (_workFolderShareLost)
        {
            RestoreShared(workFolder);
            _workFolderShareLost = false;
            return;
        }

        if (_handles.ContainsKey(workFolder))
        {
            return;
        }

        try
        {
            OpenWaiting(workFolder, workFolder, FileShare.ReadWrite);
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
    /// <exception cref="LockContentionException">期限までにワークフォルダ全体のロックを取れない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    /// <exception cref="IOException">共有へ戻すときの、共有違反以外の失敗</exception>
    internal void AcquireExclusive(string workFolder)
    {
        if (_workFolderShareLost)
        {
            RestoreShared(workFolder);
            _workFolderShareLost = false;
        }

        if (_workFolderExclusive && _handles.ContainsKey(workFolder))
        {
            return;
        }

        bool restoreSharedOnFailure = _handles.ContainsKey(workFolder);
        if (restoreSharedOnFailure)
        {
            ReleaseHandle(workFolder);
        }

        try
        {
            OpenWaiting(workFolder, workFolder, FileShare.None);
            _workFolderExclusive = true;
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
                RestoreOrMarkLost(workFolder);
            }

            throw Contention(workFolder);
        }
        catch (IOException)
        {
            if (restoreSharedOnFailure)
            {
                RestoreOrMarkLost(workFolder);
            }

            throw;
        }
        catch (UnauthorizedAccessException)
        {
            if (restoreSharedOnFailure)
            {
                RestoreOrMarkLost(workFolder);
            }

            throw;
        }
    }

    /// <summary>
    /// 自分以外の `.lock` が使用中なら、排他をやめて共有に戻す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <exception cref="LockContentionException">期限までにほかのロックが空かない</exception>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
    internal void RejectForeignLocks(string workFolder)
    {
        try
        {
            while (ForeignLocksBusy(workFolder))
            {
                if (!WaitForRetry())
                {
                    ReleaseHandle(workFolder);
                    RestoreOrMarkLost(workFolder);
                    throw Contention(workFolder);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ReleaseHandle(workFolder);
            RestoreAfterCancel(workFolder);
            throw;
        }
    }

    /// <summary>
    /// 絶対パスを大文字化して辞書順に並べ、その順でロックする。同じパスは開き直さない
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPaths">正規化した絶対パス</param>
    internal void Acquire(string workFolder, params string[] fullPaths)
    {
        List<string> ordered = Order(fullPaths);
        foreach (string fullPath in ordered)
        {
            AcquireOne(workFolder, fullPath);
        }
    }

    /// <summary>
    /// 持っているハンドルを閉じる。ロックファイルは残す
    /// </summary>
    internal void Release()
    {
        foreach (FileStream handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
        _workFolderExclusive = false;
        _workFolderShareLost = false;
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

    private static FileStream Open(string workFolder, string fullPath, FileShare share)
    {
        ThrowIfOpenArmed(share);
        string lockPath = FilePath(workFolder, fullPath);
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

    private static void ThrowIfOpenArmed(FileShare share)
    {
        Queue<(FileShare Share, Exception Exception)>? failures = _openFailures.Value;
        if (failures is null || failures.Count == 0 || failures.Peek().Share != share)
        {
            return;
        }

        throw failures.Dequeue().Exception;
    }

    private void AcquireOne(string workFolder, string fullPath)
    {
        if (_handles.ContainsKey(fullPath))
        {
            return;
        }

        try
        {
            OpenWaiting(workFolder, fullPath, FileShare.None);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new LockContentionException("他のトランザクションがこのパスを使用中です: " + fullPath, fullPath);
        }
    }

    private void ReleaseHandle(string fullPath)
    {
        if (_handles.Remove(fullPath, out FileStream? handle))
        {
            handle.Dispose();
        }

        _workFolderExclusive = false;
    }

    private void RestoreShared(string workFolder)
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
                if (!WaitForRetry())
                {
                    throw Contention(workFolder);
                }
            }
        }
    }

    private void RestoreSharedOnce(string workFolder)
    {
        if (_handles.ContainsKey(workFolder))
        {
            return;
        }

        _workFolderExclusive = false;
        _handles.Add(workFolder, Open(workFolder, workFolder, FileShare.ReadWrite));
    }

    private void OpenWaiting(string workFolder, string fullPath, FileShare share)
    {
        while (true)
        {
            try
            {
                _handles.Add(fullPath, Open(workFolder, fullPath, share));
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (!WaitForRetry())
                {
                    throw;
                }
            }
        }
    }

    private bool ForeignLocksBusy(string workFolder)
    {
        string directory = MetadataNames.LockFolderPath(workFolder);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        HashSet<string> owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fullPath in _handles.Keys)
        {
            owned.Add(FilePath(workFolder, fullPath));
        }

        foreach (string lockFile in Directory.GetFiles(directory, "*.lock"))
        {
            if (owned.Contains(lockFile))
            {
                continue;
            }

            try
            {
                using FileStream probe = new FileStream(
                    lockFile,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                return true;
            }
        }

        return false;
    }

    private bool WaitForRetry()
    {
        if (!_waitArmed || (!_waitForever && Environment.TickCount64 >= _deadlineTick))
        {
            return false;
        }

        _waitCancellation.ThrowIfCancellationRequested();
        int delay = RetryIntervalMilliseconds;
        if (!_waitForever)
        {
            long remaining = _deadlineTick - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            delay = (int)Math.Min(RetryIntervalMilliseconds, remaining);
        }

        if (_waitCancellation.CanBeCanceled)
        {
            if (_waitCancellation.WaitHandle.WaitOne(delay))
            {
                _waitCancellation.ThrowIfCancellationRequested();
            }
        }
        else
        {
            Thread.Sleep(delay);
        }

        return true;
    }

    private void RestoreAfterCancel(string workFolder)
    {
        try
        {
            RestoreSharedOnce(workFolder);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            _workFolderShareLost = true;
        }
        catch (IOException)
        {
            _workFolderShareLost = true;
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            _workFolderShareLost = true;
            throw;
        }
    }

    private void RestoreOrMarkLost(string workFolder)
    {
        try
        {
            RestoreShared(workFolder);
        }
        catch (LockContentionException)
        {
            _workFolderShareLost = true;
            throw;
        }
        catch (IOException)
        {
            _workFolderShareLost = true;
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            _workFolderShareLost = true;
            throw;
        }
    }
}
