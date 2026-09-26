namespace Txfio;

/// <summary>
/// 1 回の公開呼び出しでロックを待つ期限と、待ちを取り消すトークン
/// </summary>
/// <remarks>
/// 既定値は待たない（最初の共有違反で諦める）
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
    /// 今から数えた期限で、ロック待ちを始める
    /// </summary>
    /// <param name="lockWait">待つ上限（ゼロは待たない、<see cref="Timeout.InfiniteTimeSpan"/> は期限がない）</param>
    /// <param name="cancellationToken">待ちを取り消すトークン</param>
    /// <returns>この呼び出しのロック待ち</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockWait"/> がゼロ未満である（<see cref="Timeout.InfiniteTimeSpan"/> は除く）</exception>
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
    /// 期限の前なら少し待つ（呼び出し元のスレッドは止めない）
    /// </summary>
    /// <returns>待ったあと、もう一度試すなら <see langword="true"/>（期限を過ぎていれば待たずに <see langword="false"/>）</returns>
    /// <exception cref="OperationCanceledException">待ちのあいだに取り消された</exception>
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
