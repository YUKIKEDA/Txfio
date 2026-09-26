using System.Collections.Concurrent;
using Txfio.Tests.Support;

namespace Txfio.Tests.Threading;

public sealed class CallerContextTests
{
    /// <summary>
    /// UI スレッド相当から呼んでも、IO は呼び出し元のスレッドで行わず、await のあとは呼び出し元へ戻る
    /// </summary>
    /// <remarks>
    /// <para>前提: 1 本のスレッドで続きを動かす SynchronizationContext の上にいる</para>
    /// <para>手順: そのスレッドから AddAsync し、ロックファイルを開いたスレッドを記録する</para>
    /// <para>期待: ロックファイルを開いたのは呼び出し元のスレッドではなく、await のあとは呼び出し元のスレッドで続く</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_UIスレッド相当から呼んでもそのスレッドでIOしないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        RecordingFaults faults = new RecordingFaults();
        using SingleThreadContext context = new SingleThreadContext();

        (int caller, int resumed) = await context.RunAsync(async () =>
        {
            int callerThread = Environment.CurrentManagedThreadId;
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path, faults);
            await using MemoryStream content = new MemoryStream(new byte[] { 1, 2, 3 });
            await tx.AddAsync("a.bin", content);
            return (callerThread, Environment.CurrentManagedThreadId);
        });

        Assert.NotEmpty(faults.OpenThreads);
        Assert.DoesNotContain(caller, faults.OpenThreads);
        Assert.Equal(caller, resumed);
    }

    /// <summary>
    /// コンテキストが無いときは、スレッドプールへ移らずにそのまま続ける
    /// </summary>
    /// <remarks>
    /// <para>前提: SynchronizationContext が無く、既定の TaskScheduler の上にいる</para>
    /// <para>手順: CallerContext.LeaveAsync の await 先を見る</para>
    /// <para>期待: 既に完了しており、移らない</para>
    /// </remarks>
    [Fact]
    public async Task LeaveAsync_コンテキストが無ければ移らないこと()
    {
        await Task.Run(() =>
        {
            Assert.Null(SynchronizationContext.Current);
            Assert.True(CallerContext.LeaveAsync().GetAwaiter().IsCompleted);
        });
    }

    private sealed class RecordingFaults : IFaultInjector
    {
        public ConcurrentBag<int> OpenThreads { get; } = new ConcurrentBag<int>();

        public bool ShouldSkipRollback => false;

        public void CheckPoint(string name)
        {
            _ = name;
        }

        public void ThrowIfApplyArmed()
        {
        }

        public void ThrowIfOpenArmed(FileShare share)
        {
            _ = share;
            OpenThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue =
            new BlockingCollection<(SendOrPostCallback Callback, object? State)>();

        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(Pump) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public Task<T> RunAsync<T>(Func<Task<T>> body)
        {
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                async _ =>
                {
                    try
                    {
                        completion.SetResult(await body());
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                },
                null);
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
