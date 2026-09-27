namespace Txfio;

/// <summary>
/// The deadline for waiting on locks in one public call, and the token that cancels the wait.
/// </summary>
/// <remarks>
/// The default does not wait (it gives up at the first sharing violation).
/// </remarks>
internal readonly struct LockAttempt
{
    private const int RetryIntervalMilliseconds = 100;

    private readonly bool _armed;

    private readonly bool _waitForever;

    private readonly long _deadlineTick;

    private readonly CancellationToken _cancellationToken;

    private LockAttempt(bool waitForever, long deadlineTick, CancellationToken cancellationToken)
    {
        _armed = true;
        _waitForever = waitForever;
        _deadlineTick = deadlineTick;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Starts waiting on locks, with a deadline counted from now.
    /// </summary>
    /// <param name="lockWait">How long to wait (zero does not wait; <see cref="Timeout.InfiniteTimeSpan"/> has no deadline).</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>The lock wait of this call.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    internal static LockAttempt Start(TimeSpan lockWait, CancellationToken cancellationToken)
    {
        if (lockWait < TimeSpan.Zero && lockWait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(lockWait));
        }

        if (lockWait == Timeout.InfiniteTimeSpan)
        {
            return new LockAttempt(waitForever: true, deadlineTick: 0, cancellationToken);
        }

        long now = Environment.TickCount64;
        if (lockWait <= TimeSpan.Zero)
        {
            return new LockAttempt(waitForever: false, now, cancellationToken);
        }

        long milliseconds = (long)lockWait.TotalMilliseconds;
        long deadline = milliseconds > long.MaxValue - now ? long.MaxValue : now + milliseconds;
        return new LockAttempt(waitForever: false, deadline, cancellationToken);
    }

    /// <summary>
    /// Waits a little if the deadline has not passed (without blocking the caller's thread).
    /// </summary>
    /// <returns><see langword="true"/> to try again after waiting (<see langword="false"/> without waiting if the deadline has passed).</returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    internal async Task<bool> WaitForRetryAsync()
    {
        if (!_armed || (!_waitForever && Environment.TickCount64 >= _deadlineTick))
        {
            return false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int delay = RetryIntervalMilliseconds;
        if (!_waitForever)
        {
            long remaining = _deadlineTick - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            delay = (int)Math.Min(RetryIntervalMilliseconds, remaining);
        }

        try
        {
            await Task.Delay(delay, _cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        return true;
    }
}
