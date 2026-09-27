namespace Txfio.Tests.Lock;

public sealed class LockAttemptTests
{
    /// <summary>
    /// The default and a zero wait give up without waiting.
    /// </summary>
    /// <remarks>
    /// <para>Given: a default LockAttempt, and a LockAttempt started with TimeSpan.Zero.</para>
    /// <para>When: WaitForRetryAsync is called on each.</para>
    /// <para>Then: both return <see langword="false"/>.</para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_DefaultAndZeroDoNotWait()
    {
        LockAttempt none = default;
        LockAttempt zero = LockAttempt.Start(TimeSpan.Zero, CancellationToken.None);

        Assert.False(await none.WaitForRetryAsync());
        Assert.False(await zero.WaitForRetryAsync());
    }

    /// <summary>
    /// A wait without a deadline also throws OperationCanceledException when canceled.
    /// </summary>
    /// <remarks>
    /// <para>Given: a LockAttempt with Timeout.InfiniteTimeSpan is started with a canceled token.</para>
    /// <para>When: WaitForRetryAsync is called.</para>
    /// <para>Then: OperationCanceledException.</para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_ThrowsWhenCanceled()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        LockAttempt attempt = LockAttempt.Start(Timeout.InfiniteTimeSpan, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitForRetryAsync());
    }

    /// <summary>
    /// Within the deadline, it waits and tries again.
    /// </summary>
    /// <remarks>
    /// <para>Given: a one-minute LockAttempt.</para>
    /// <para>When: WaitForRetryAsync is called.</para>
    /// <para>Then: <see langword="true"/>.</para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_TriesAgainWithinDeadline()
    {
        LockAttempt attempt = LockAttempt.Start(TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(await attempt.WaitForRetryAsync());
    }

    /// <summary>
    /// A negative wait is rejected.
    /// </summary>
    /// <remarks>
    /// <para>Given: nothing.</para>
    /// <para>When: Start is called with -2 milliseconds (-1 millisecond is Timeout.InfiniteTimeSpan).</para>
    /// <para>Then: ArgumentOutOfRangeException.</para>
    /// </remarks>
    [Fact]
    public void Start_RejectsNegativeWait()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LockAttempt.Start(TimeSpan.FromMilliseconds(-2), CancellationToken.None));
    }
}
