using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Txfio;

/// <summary>
/// Renames moves at commit and recovery, without turning them into copy and delete.
/// </summary>
internal static partial class SameVolumeMove
{
    private const uint MoveFileReplaceExisting = 0x1;

    /// <summary>
    /// Moves a file with a rename.
    /// </summary>
    /// <param name="sourcePath">The source.</param>
    /// <param name="destPath">The destination.</param>
    /// <param name="replace">When <see langword="true"/>, replaces an existing file at the destination (<c>MOVEFILE_REPLACE_EXISTING</c>).</param>
    /// <exception cref="IOException">It cannot be renamed.</exception>
    internal static void MoveFile(string sourcePath, string destPath, bool replace = false)
    {
        Move(sourcePath, destPath, directory: false, replace);
    }

    /// <summary>
    /// Moves a directory with a rename.
    /// </summary>
    /// <param name="sourcePath">The source.</param>
    /// <param name="destPath">The destination.</param>
    /// <exception cref="IOException">It cannot be renamed.</exception>
    internal static void MoveDirectory(string sourcePath, string destPath)
    {
        Move(sourcePath, destPath, directory: true, replace: false);
    }

    private static void Move(string sourcePath, string destPath, bool directory, bool replace)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (directory)
            {
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                File.Move(sourcePath, destPath, replace);
            }

            return;
        }

        // Do not add the flag that allows a copy (with it, another volume would mean copy and delete).
        uint flags = replace ? MoveFileReplaceExisting : 0;
        if (!MoveFileEx(ExtendIfNeeded(sourcePath), ExtendIfNeeded(destPath), flags))
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
