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

    private readonly Dictionary<string, FileStream> _handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

    private bool _workFolderExclusive;

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
    /// ワークフォルダの哨兵を共有で開く。既に持っていれば開き直さない
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    internal void AcquireShared(string workFolder)
    {
        if (_handles.ContainsKey(workFolder))
        {
            return;
        }

        try
        {
            _handles.Add(workFolder, Open(workFolder, workFolder, FileShare.ReadWrite));
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw Contention(workFolder);
        }
    }

    /// <summary>
    /// ワークフォルダの哨兵を排他で開く。共有を持っていれば閉じて取り直す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <exception cref="LockContentionException">他のトランザクションが哨兵を持っている</exception>
    internal void AcquireExclusive(string workFolder)
    {
        if (_workFolderExclusive && _handles.ContainsKey(workFolder))
        {
            return;
        }

        if (_handles.ContainsKey(workFolder))
        {
            ReleaseHandle(workFolder);
            try
            {
                _handles.Add(workFolder, Open(workFolder, workFolder, FileShare.None));
                _workFolderExclusive = true;
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                RestoreShared(workFolder);
                throw Contention(workFolder);
            }
        }

        try
        {
            _handles.Add(workFolder, Open(workFolder, workFolder, FileShare.None));
            _workFolderExclusive = true;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw Contention(workFolder);
        }
    }

    /// <summary>
    /// 自分以外の `.lock` が使用中なら、排他をやめて共有に戻す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <exception cref="LockContentionException">他のトランザクションがロックを持っている</exception>
    internal void RejectForeignLocks(string workFolder)
    {
        string directory = MetadataNames.LockFolderPath(workFolder);
        if (!Directory.Exists(directory))
        {
            return;
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
                ReleaseHandle(workFolder);
                RestoreShared(workFolder);
                throw Contention(workFolder);
            }
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

    private static bool IsSharingViolation(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code == SharingViolation || code == LockViolation;
    }

    private static LockContentionException Contention(string workFolder)
    {
        return new LockContentionException("他のトランザクションがこのパスを使用中です: " + workFolder, workFolder);
    }

    private static FileStream Open(string workFolder, string fullPath, FileShare share)
    {
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

    private void AcquireOne(string workFolder, string fullPath)
    {
        if (_handles.ContainsKey(fullPath))
        {
            return;
        }

        try
        {
            _handles.Add(fullPath, Open(workFolder, fullPath, FileShare.None));
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
        _workFolderExclusive = false;
        try
        {
            if (!_handles.ContainsKey(workFolder))
            {
                _handles.Add(workFolder, Open(workFolder, workFolder, FileShare.ReadWrite));
            }
        }
        catch (IOException)
        {
            // 共有に戻せなくても、排他は持たない
        }
    }
}
