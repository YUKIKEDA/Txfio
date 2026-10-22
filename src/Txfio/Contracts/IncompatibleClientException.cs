namespace Txfio;

/// <summary>
/// A client of another on-disk protocol version is using the work folder, so this client does not touch it.
/// </summary>
/// <remarks>
/// Clients that share a work folder must use the same protocol version. The message names both protocol versions and the work folder.
/// See "The protocol lock" in the design document.
/// </remarks>
public sealed class IncompatibleClientException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IncompatibleClientException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public IncompatibleClientException(string message)
        : base(message)
    {
    }
}
