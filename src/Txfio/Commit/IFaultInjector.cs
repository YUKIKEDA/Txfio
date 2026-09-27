namespace Txfio;

/// <summary>
/// Failures and partial stops that a test passes to each transaction.
/// </summary>
internal interface IFaultInjector
{
    /// <summary>
    /// Right after <c>Committing</c> is written.
    /// </summary>
    const string AfterCommitting = "AfterCommitting";

    /// <summary>
    /// Right after each operation in apply order succeeds.
    /// </summary>
    const string AfterApply = "AfterApply";

    /// <summary>
    /// Gets a value indicating whether a commit or a rollback was stopped partway (then Dispose does not roll back).
    /// </summary>
    bool ShouldSkipRollback { get; }

    /// <summary>
    /// Throws as a stop if the given point has been reached.
    /// </summary>
    /// <param name="name">The current point.</param>
    void CheckPoint(string name);

    /// <summary>
    /// Throws the exception armed for the next apply, once, if there is one.
    /// </summary>
    void ThrowIfApplyArmed();

    /// <summary>
    /// Throws the armed exception, if any, when opening with the armed share mode.
    /// </summary>
    /// <param name="share">The share mode to open with.</param>
    void ThrowIfOpenArmed(FileShare share);
}
