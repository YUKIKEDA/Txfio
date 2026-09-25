using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Txfio;

/// <summary>
/// コミットと復旧の移動を、コピーと削除にせず rename する
/// </summary>
internal static partial class SameVolumeMove
{
    /// <summary>
    /// ファイルを rename で移動する
    /// </summary>
    /// <param name="sourcePath">移動元</param>
    /// <param name="destPath">移動先</param>
    /// <exception cref="IOException">rename できない</exception>
    internal static void MoveFile(string sourcePath, string destPath)
    {
        Move(sourcePath, destPath, directory: false);
    }

    /// <summary>
    /// ディレクトリを rename で移動する
    /// </summary>
    /// <param name="sourcePath">移動元</param>
    /// <param name="destPath">移動先</param>
    /// <exception cref="IOException">rename できない</exception>
    internal static void MoveDirectory(string sourcePath, string destPath)
    {
        Move(sourcePath, destPath, directory: true);
    }

    private static void Move(string sourcePath, string destPath, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (directory)
            {
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                File.Move(sourcePath, destPath);
            }

            return;
        }

        // フラグは 0 のままにする（コピーを許すと別ボリュームでコピーと削除になる）
        if (!MoveFileEx(ExtendIfNeeded(sourcePath), ExtendIfNeeded(destPath), 0))
        {
            ThrowLastError();
        }
    }

    private static void ThrowLastError()
    {
        int error = Marshal.GetLastPInvokeError();
        int hresult = error <= 0 ? error : unchecked((int)(0x80070000 | error));
        throw new IOException(new Win32Exception(error).Message, hresult);
    }

    private static string ExtendIfNeeded(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string sourcePath, string destPath, uint flags);
}
