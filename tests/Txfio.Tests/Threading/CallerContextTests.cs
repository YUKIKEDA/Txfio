using System.Collections.Concurrent;
using Txfio.Tests.Support;

namespace Txfio.Tests.Threading;

public sealed class CallerContextTests
{
    /// <summary>
    /// Even when called from the equivalent of a UI thread, IO does not run on the caller's thread, and after await it returns to the caller.
    /// </summary>
    /// <remarks>
    /// <para>Given: running on a SynchronizationContext that runs continuations on one thread.</para>
    /// <para>When: AddAsync is called from that thread, and the thread that opened the lock file is recorded.</para>
    /// <para>Then: the lock file was not opened on the caller's thread, and after await execution continues on the caller's thread.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_DoesNotDoIoOnUiLikeThread()
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
    /// Without a context, it continues without switching to the thread pool.
    /// </summary>
    /// <remarks>
    /// <para>Given: no SynchronizationContext, on the default TaskScheduler.</para>
    /// <para>When: the await target of CallerContext.LeaveAsync is checked.</para>
    /// <para>Then: it is already completed and does not switch.</para>
    /// </remarks>
    [Fact]
    public async Task LeaveAsync_DoesNotSwitchWithoutContext()
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
