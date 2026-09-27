namespace Txfio;

/// <summary>
/// The <c>.txfio/tx-{guid}.lock</c> held while a transaction is alive.
/// </summary>
internal static class LivenessLock
{
    /// <summary>
    /// Creates and opens the liveness lock when a transaction begins.
    /// </summary>
    /// <param name="lockPath">The path of the liveness lock.</param>
    /// <returns>The handle held until the transaction ends.</returns>
    internal static FileStream Create(string lockPath)
    {
        return Open(lockPath, FileMode.CreateNew);
    }

    /// <summary>
    /// Opens the liveness lock of a journal without an owner (creates the file if missing).
    /// </summary>
    /// <param name="lockPath">The path of the liveness lock.</param>
    /// <returns>The opened handle (<see langword="null"/> if the owner is alive).</returns>
    internal static FileStream? TryOpenStale(string lockPath)
    {
        try
        {
            return Open(lockPath, FileMode.OpenOrCreate);
        }
        catch (IOException exception) when (PathLockSet.IsSharingViolation(exception))
        {
            return null;
        }
    }

    private static FileStream Open(string lockPath, FileMode mode)
    {
        return new FileStream(
            lockPath,
            mode,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }
}
