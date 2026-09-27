namespace Txfio;

/// <summary>
/// One progress report of a copy.
/// </summary>
public readonly record struct TransferProgress
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferProgress"/> struct with the bytes written and the bytes remaining at the start.
    /// </summary>
    /// <param name="bytesCopied">The bytes written to <c>.txnew</c> so far.</param>
    /// <param name="totalBytes">The bytes remaining at the start (<see langword="null"/> when unknown).</param>
    public TransferProgress(long bytesCopied, long? totalBytes)
    {
        BytesCopied = bytesCopied;
        TotalBytes = totalBytes;
    }

    /// <summary>
    /// Gets the bytes written to <c>.txnew</c> so far.
    /// </summary>
    public long BytesCopied { get; }

    /// <summary>
    /// Gets the bytes remaining at the start (<see langword="null"/> when unknown).
    /// </summary>
    public long? TotalBytes { get; }
}
