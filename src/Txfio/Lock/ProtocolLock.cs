using System.Globalization;

namespace Txfio;

/// <summary>
/// The <c>.txfio/proto-{N}.lock</c> that a client holds while it holds the work-folder lock, so that clients of another protocol version can see it.
/// </summary>
/// <remarks>
/// The file name, the open modes, and the steps of the check are the same in every version of the library.
/// Changing them would make clients of different versions unable to see each other.
/// </remarks>
internal static class ProtocolLock
{
    /// <summary>
    /// The on-disk protocol version of this library.
    /// </summary>
    internal const int Version = 1;

    private const string Prefix = "proto-";

    private const string Suffix = ".lock";

    // A probe only has the file open for a moment, so retry the open of its own file at least this long.
    private const int OwnOpenGraceMilliseconds = 1000;

    /// <summary>
    /// Returns the path of the protocol lock of a version.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="version">The protocol version.</param>
    /// <returns>The path of <c>.txfio/proto-{N}.lock</c>.</returns>
    internal static string FilePath(string workFolder, int version)
    {
        return System.IO.Path.Combine(
            MetadataNames.FolderPath(workFolder),
            Prefix + version.ToString(CultureInfo.InvariantCulture) + Suffix);
    }

    /// <summary>
    /// Checks that no client of another protocol version is alive, and opens this version's protocol lock as shared.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="attempt">The lock wait of this call.</param>
    /// <returns>The handle to hold as long as the work-folder lock.</returns>
    /// <exception cref="IncompatibleClientException">A client of another protocol version is still alive at the deadline.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    internal static async Task<FileStream> AcquireAsync(string workFolder, LockAttempt attempt)
    {
        string ownPath = FilePath(workFolder, Version);
        while (true)
        {
            // Probe before opening its own file, so a waiting client does not look alive to the clients in use.
            string? other = FindLiveOther(workFolder, ownPath);
            if (other is null)
            {
                FileStream own = await OpenOwnAsync(workFolder, ownPath, attempt).ConfigureAwait(false);

                // Probe again while holding it: of two versions that start at once, at least one sees the other.
                other = FindLiveOther(workFolder, ownPath);
                if (other is null)
                {
                    return own;
                }

                own.Dispose();
            }

            if (!await attempt.WaitForRetryAsync().ConfigureAwait(false))
            {
                throw Incompatible(workFolder, other);
            }
        }
    }

    private static async Task<FileStream> OpenOwnAsync(string workFolder, string ownPath, LockAttempt attempt)
    {
        Directory.CreateDirectory(MetadataNames.FolderPath(workFolder));
        long graceEnd = long.MinValue;
        while (true)
        {
            try
            {
                return new FileStream(ownPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException exception) when (PathLockSet.IsSharingViolation(exception))
            {
                // Holders of this version share the file, so only another version's probe can be in the way.
                if (graceEnd == long.MinValue)
                {
                    graceEnd = Environment.TickCount64 + OwnOpenGraceMilliseconds;
                }

                if (!await attempt.WaitForRetryAsync(graceEnd).ConfigureAwait(false))
                {
                    throw Incompatible(workFolder, null);
                }
            }
        }
    }

    // Returns the version text of a live client of another version, or null when none is alive.
    private static string? FindLiveOther(string workFolder, string ownPath)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return null;
        }

        string[] files = Directory.GetFiles(metadataFolder, Prefix + "*" + Suffix, SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            string? version = VersionText(System.IO.Path.GetFileName(path));
            if (version is null || string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsHeld(path))
            {
                return version;
            }
        }

        return null;
    }

    // A probe with read access and FileShare.Read fails only while a holder has the file open for writing; two probes do not block each other.
    private static bool IsHeld(string path)
    {
        try
        {
            using FileStream probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException exception) when (PathLockSet.IsSharingViolation(exception))
        {
            return true;
        }
    }

    // The {N} of proto-{N}.lock when it is decimal digits, otherwise null.
    private static string? VersionText(string name)
    {
        if (name.Length <= Prefix.Length + Suffix.Length
            || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string digits = name.Substring(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
        foreach (char c in digits)
        {
            if (c < '0' || c > '9')
            {
                return null;
            }
        }

        return digits;
    }

    private static IncompatibleClientException Incompatible(string workFolder, string? other)
    {
        string own = Version.ToString(CultureInfo.InvariantCulture);
        string others = other is null ? "another protocol" : "protocol " + other;
        return new IncompatibleClientException(
            "A client of another protocol version is using the work folder (this client: protocol " + own + ", other: " + others + "): " + workFolder);
    }
}
