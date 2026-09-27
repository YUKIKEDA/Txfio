namespace Txfio;

/// <summary>
/// The base of the exceptions Txfio returns to the caller.
/// </summary>
public abstract class TxfioException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TxfioException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    protected TxfioException(string message)
        : base(message)
    {
    }
}
