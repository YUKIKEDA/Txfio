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

    private void AcquireOne(string workFolder, string fullPath)
    {
        if (_handles.ContainsKey(fullPath))
        {
            return;
        }

        string lockPath = FilePath(workFolder, fullPath);
        string? directory = System.IO.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            FileStream stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            _handles.Add(fullPath, stream);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new LockContentionException("他のトランザクションがこのパスを使用中です: " + fullPath, fullPath);
        }
    }
}
