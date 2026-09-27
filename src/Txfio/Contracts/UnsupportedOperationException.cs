namespace Txfio;

/// <summary>
/// An operation the library does not support.
/// </summary>
public sealed class UnsupportedOperationException : TxfioException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedOperationException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public UnsupportedOperationException(string message)
        : base(message)
    {
    }
}
