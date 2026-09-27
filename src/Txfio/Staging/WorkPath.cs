using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Txfio;

/// <summary>
/// Normalizes paths in the work folder.
/// </summary>
internal static class WorkPath
{
    /// <summary>
    /// Normalizes a path to an absolute path based on the work folder.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="path">The relative or absolute target path.</param>
    /// <returns>The normalized absolute path.</returns>
    /// <exception cref="ArgumentException">The path points outside the work folder.</exception>
    /// <exception cref="InvalidOperationException">The target itself, or an ancestor other than the work folder itself, is a reparse point.</exception>
    /// <exception cref="IOException">The long name of an existing component cannot be obtained.</exception>
    internal static string ResolveInWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string combined = System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(workFolder, path);
        string fullPath = ToLongPath(System.IO.Path.GetFullPath(combined));
        if (!IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("The path must be inside the work folder", nameof(path));
        }

        ThrowIfReparseInside(workFolder, fullPath);
        return fullPath;
    }

    /// <summary>
    /// Normalizes a path to an absolute path outside the work folder.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="path">An absolute path, or a path relative to the current directory.</param>
    /// <returns>The normalized absolute path.</returns>
    /// <exception cref="ArgumentException">The path points inside the work folder.</exception>
    internal static string ResolveOutsideWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        if (IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("The path must be outside the work folder", nameof(path));
        }

        return fullPath;
    }

    /// <summary>
    /// Returns whether a path is the metadata folder itself or under it.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="fullPath">The normalized absolute path.</param>
    /// <returns><see langword="true"/> if it is the metadata folder itself or under it.</returns>
    internal static bool IsInMetadataFolder(string workFolder, string fullPath)
    {
        return PathMath.IsEqualOrUnder(MetadataNames.FolderPath(workFolder), fullPath);
    }

    /// <summary>
    /// Returns the <c>.txnew</c> path in the same directory as the target file.
    /// </summary>
    /// <param name="targetPath">The absolute path of the target file.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <returns>The path of the staging file.</returns>
    internal static string StagingFilePath(string targetPath, Guid transactionId)
    {
        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The parent directory of the target path cannot be determined", nameof(targetPath));
        }

        string fileName = System.IO.Path.GetFileName(targetPath);
        return System.IO.Path.Combine(
            directory,
            fileName + "." + transactionId.ToString("D") + ".txnew");
    }

    /// <summary>
    /// Returns the path to move the existing destination aside to, when swapping a directory.
    /// </summary>
    /// <param name="destPath">The destination being swapped.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <returns><c>{name}.{txid}.txold</c> in the same parent.</returns>
    internal static string ReplacedDirectoryPath(string destPath, Guid transactionId)
    {
        string trimmed = System.IO.Path.TrimEndingDirectorySeparator(destPath);
        return trimmed + "." + transactionId.ToString("D") + ".txold";
    }

    /// <summary>
    /// Returns the path to back up the original <c>.txnew</c> to during a restage.
    /// </summary>
    /// <param name="stagingPath">The operation's <c>.txnew</c>.</param>
    /// <returns>The <c>.txnew</c> path with <c>.prev</c> appended.</returns>
    internal static string StagingBackupPath(string stagingPath)
    {
        return stagingPath + ".prev";
    }

    /// <summary>
    /// Returns whether a path is a <c>.txnew</c> of this transaction.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <returns><see langword="true"/> if it is a <c>.txnew</c> of this transaction.</returns>
    internal static bool IsThisTransactionStagingFile(string path, Guid transactionId)
    {
        return path.EndsWith(
            "." + transactionId.ToString("D") + ".txnew",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes existing components to their long names (trailing names that do not exist yet are kept as they are).
    /// </summary>
    /// <param name="fullPath">The absolute path.</param>
    /// <returns>The absolute path with long names.</returns>
    /// <exception cref="IOException">The long name of an existing component cannot be obtained.</exception>
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
    /// Returns whether a path is a reparse point (a symbolic link or a junction).
    /// </summary>
    /// <param name="path">The path to check (throws if it does not exist).</param>
    /// <returns><see langword="true"/> if it is a reparse point.</returns>
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
                throw new InvalidOperationException("A reparse point cannot be used: " + trimmed);
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
