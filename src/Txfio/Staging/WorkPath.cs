using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Txfio;

/// <summary>
/// ワークフォルダ内のパス正規化
/// </summary>
internal static class WorkPath
{
    /// <summary>
    /// パスをワークフォルダ基準の絶対パスに正規化する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="path">相対または絶対の対象パス</param>
    /// <returns>正規化した絶対パス</returns>
    /// <exception cref="ArgumentException">ワークフォルダの外側を指している</exception>
    /// <exception cref="InvalidOperationException">対象自身、またはワークフォルダ自身を除く祖先がリパースポイントである</exception>
    /// <exception cref="IOException">存在する要素の長い名前を取れない</exception>
    internal static string ResolveInWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string combined = System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(workFolder, path);
        string fullPath = ToLongPath(System.IO.Path.GetFullPath(combined));
        if (!IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("パスはワークフォルダの内側である必要があります", nameof(path));
        }

        ThrowIfReparseInside(workFolder, fullPath);
        return fullPath;
    }

    /// <summary>
    /// パスをワークフォルダの外の絶対パスに正規化する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="path">絶対パス、または現在ディレクトリ基準の相対パス</param>
    /// <returns>正規化した絶対パス</returns>
    /// <exception cref="ArgumentException">ワークフォルダの内側を指している</exception>
    internal static string ResolveOutsideWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        if (IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("パスはワークフォルダの外側である必要があります", nameof(path));
        }

        return fullPath;
    }

    /// <summary>
    /// メタデータフォルダそのもの、またはその配下かどうかを判定する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPath">正規化した絶対パス</param>
    /// <returns>メタデータフォルダそのもの、またはその配下なら <see langword="true"/></returns>
    internal static bool IsInMetadataFolder(string workFolder, string fullPath)
    {
        return PathMath.IsEqualOrUnder(MetadataNames.FolderPath(workFolder), fullPath);
    }

    /// <summary>
    /// 対象ファイルと同じディレクトリの `.txnew` パスを返す
    /// </summary>
    /// <param name="targetPath">対象ファイルの絶対パス</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <returns>ステージングファイルのパス</returns>
    internal static string StagingFilePath(string targetPath, Guid transactionId)
    {
        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("対象パスの親ディレクトリを特定できません", nameof(targetPath));
        }

        string fileName = System.IO.Path.GetFileName(targetPath);
        return System.IO.Path.Combine(
            directory,
            fileName + "." + transactionId.ToString("D") + ".txnew");
    }

    /// <summary>
    /// このトランザクションの `.txnew` かどうかを判定する
    /// </summary>
    /// <param name="path">調べるパス</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <returns>このトランザクションの `.txnew` なら <see langword="true"/></returns>
    internal static bool IsThisTransactionStagingFile(string path, Guid transactionId)
    {
        return path.EndsWith(
            "." + transactionId.ToString("D") + ".txnew",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 存在する要素を長い名前へ揃える（まだ無い末尾の名前はそのまま残す）
    /// </summary>
    /// <param name="fullPath">絶対パス</param>
    /// <returns>長い名前へ揃えた絶対パス</returns>
    /// <exception cref="IOException">存在する要素の長い名前を取れない</exception>
    internal static string ToLongPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        string? suffix = null;
        string current = fullPath;
        while (true)
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(current);
            if (TryQueryLongPath(trimmed, out string longPath))
            {
                return suffix is null ? longPath : System.IO.Path.Combine(longPath, suffix);
            }

            string? parent = System.IO.Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            string name = System.IO.Path.GetFileName(trimmed);
            suffix = suffix is null ? name : System.IO.Path.Combine(name, suffix);
            current = parent;
        }
    }

    /// <summary>
    /// パスがリパースポイント（シンボリックリンクやジャンクション）かどうかを判定する
    /// </summary>
    /// <param name="path">調べるパス（存在しなければ例外）</param>
    /// <returns>リパースポイントなら <see langword="true"/></returns>
    internal static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool TryQueryLongPath(string path, out string longPath)
    {
        var buffer = new StringBuilder(Math.Max(path.Length + 1, 260));
        while (true)
        {
            uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            if (length == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 2 or 3)
                {
                    longPath = string.Empty;
                    return false;
                }

                int hresult = error <= 0 ? error : unchecked((int)(0x80070000 | error));
                throw new IOException(new Win32Exception(error).Message, hresult);
            }

            if (length < buffer.Capacity)
            {
                longPath = buffer.ToString();
                return true;
            }

            buffer.Capacity = (int)length;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    private static void ThrowIfReparseInside(string workFolder, string fullPath)
    {
        string root = System.IO.Path.TrimEndingDirectorySeparator(workFolder);
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(current);
            if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsExistingReparsePoint(trimmed))
            {
                throw new InvalidOperationException("リパースポイントは操作できません: " + trimmed);
            }

            current = System.IO.Path.GetDirectoryName(trimmed);
        }
    }

    private static bool IsExistingReparsePoint(string path)
    {
        try
        {
            return IsReparsePoint(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsInsideWorkFolder(string workFolder, string fullPath)
    {
        return PathMath.IsUnder(workFolder, fullPath);
    }
}
