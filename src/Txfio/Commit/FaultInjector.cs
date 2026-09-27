namespace Txfio;

/// <summary>
/// Lets a test arm failures and partial stops.
/// </summary>
internal sealed class FaultInjector : IFaultInjector
{
    private readonly Queue<(FileShare Share, Exception Exception)> _openFailures = new Queue<(FileShare Share, Exception Exception)>();

    private string? _armedName;

    private bool _injected;

    private Exception? _applyFailure;

    /// <inheritdoc />
    public bool ShouldSkipRollback => _injected;

    /// <inheritdoc />
    public void CheckPoint(string name)
    {
        if (_armedName is null || _injected || !string.Equals(_armedName, name, StringComparison.Ordinal))
        {
            return;
        }

        _injected = true;
        throw new CrashInjectionException(name);
    }

    /// <inheritdoc />
    public void ThrowIfApplyArmed()
    {
        if (_applyFailure is null)
        {
            return;
        }

        Exception exception = _applyFailure;
        _applyFailure = null;
        throw exception;
    }

    /// <inheritdoc />
    public void ThrowIfOpenArmed(FileShare share)
    {
        if (_openFailures.Count == 0 || _openFailures.Peek().Share != share)
        {
            return;
        }

        throw _openFailures.Dequeue().Exception;
    }

    /// <summary>
    /// Sets the point to stop at the next time it is passed.
    /// </summary>
    /// <param name="name">The point to stop at.</param>
    internal void Arm(string name)
    {
        _armedName = name;
        _injected = false;
    }

    /// <summary>
    /// Clears the point to stop at.
    /// </summary>
    internal void Reset()
    {
        _armedName = null;
        _injected = false;
        _applyFailure = null;
        _openFailures.Clear();
    }

    /// <summary>
    /// Makes the next Dispose only close the locks.
    /// </summary>
    internal void SuppressRollback()
    {
        _armedName = "suppress";
        _injected = true;
    }

    /// <summary>
    /// Throws the given exception at the next apply.
    /// </summary>
    /// <param name="exception">The exception to throw.</param>
    internal void FailNextApply(Exception exception)
    {
        _applyFailure = exception;
    }

    /// <summary>
    /// Throws the given exception the next time a file is opened with the given share mode.
    /// </summary>
    /// <param name="share">The share mode to fail.</param>
    /// <param name="exception">The exception to throw.</param>
    internal void FailNextOpen(FileShare share, Exception exception)
    {
        _openFailures.Enqueue((share, exception));
    }

    /// <summary>
    /// Clears the armed open failure.
    /// </summary>
    internal void ClearOpenFailures()
    {
        _openFailures.Clear();
    }
}
