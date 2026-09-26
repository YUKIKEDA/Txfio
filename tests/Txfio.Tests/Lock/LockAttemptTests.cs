namespace Txfio.Tests.Lock;

public sealed class LockAttemptTests
{
    /// <summary>
    /// 既定値と待ち時間ゼロは待たずに諦める
    /// </summary>
    /// <remarks>
    /// <para>前提: 既定値の LockAttempt と、TimeSpan.Zero で始めた LockAttempt がある</para>
    /// <para>手順: それぞれ WaitForRetryAsync する</para>
    /// <para>期待: どちらも <see langword="false"/></para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_既定値とゼロは待たないこと()
    {
        LockAttempt none = default;
        LockAttempt zero = LockAttempt.Start(TimeSpan.Zero, CancellationToken.None);

        Assert.False(await none.WaitForRetryAsync());
        Assert.False(await zero.WaitForRetryAsync());
    }

    /// <summary>
    /// 期限が無い待ちも、取り消せば OperationCanceledException になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 取り消し済みのトークンで、Timeout.InfiniteTimeSpan の LockAttempt を始めている</para>
    /// <para>手順: WaitForRetryAsync する</para>
    /// <para>期待: OperationCanceledException</para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_取り消すと例外になること()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        LockAttempt attempt = LockAttempt.Start(Timeout.InfiniteTimeSpan, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitForRetryAsync());
    }

    /// <summary>
    /// 期限の中では待ってからもう一度試す
    /// </summary>
    /// <remarks>
    /// <para>前提: 1 分の LockAttempt がある</para>
    /// <para>手順: WaitForRetryAsync する</para>
    /// <para>期待: <see langword="true"/></para>
    /// </remarks>
    [Fact]
    public async Task WaitForRetryAsync_期限の中ではもう一度試すこと()
    {
        LockAttempt attempt = LockAttempt.Start(TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(await attempt.WaitForRetryAsync());
    }

    /// <summary>
    /// ゼロ未満の待ち時間は拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: なし</para>
    /// <para>手順: -2 ミリ秒で Start する（-1 ミリ秒は Timeout.InfiniteTimeSpan）</para>
    /// <para>期待: ArgumentOutOfRangeException</para>
    /// </remarks>
    [Fact]
    public void Start_ゼロ未満は拒否すること()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LockAttempt.Start(TimeSpan.FromMilliseconds(-2), CancellationToken.None));
    }
}
