using System.Runtime.CompilerServices;

namespace Txfio;

/// <summary>
/// Keeps public async methods from doing IO on the caller's thread (such as a UI thread).
/// </summary>
internal static class CallerContext
{
    /// <summary>
    /// Switches to the thread pool if the caller has a <see cref="SynchronizationContext"/> or a non-default <see cref="TaskScheduler"/>.
    /// </summary>
    /// <remarks>
    /// If it has neither (on a server or console, or already on the thread pool), it continues without switching.
    /// </remarks>
    /// <returns>A value that continues on the thread pool when awaited.</returns>
    internal static LeaveAwaitable LeaveAsync()
    {
        return default;
    }

    /// <summary>
    /// The await target of <see cref="LeaveAsync"/>.
    /// </summary>
    internal readonly struct LeaveAwaitable : ICriticalNotifyCompletion
    {
        /// <summary>
        /// Gets a value indicating whether no switch is needed.
        /// </summary>
        public bool IsCompleted => SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;

        /// <summary>
        /// Returns itself, for await.
        /// </summary>
        /// <returns>This value.</returns>
        public LeaveAwaitable GetAwaiter()
        {
            return this;
        }

        /// <summary>
        /// Runs the continuation on the thread pool.
        /// </summary>
        /// <param name="continuation">The continuation.</param>
        public void OnCompleted(Action continuation)
        {
            ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);
        }

        /// <summary>
        /// Runs the continuation on the thread pool (the execution context flows).
        /// </summary>
        /// <param name="continuation">The continuation.</param>
        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        /// <summary>
        /// Returns nothing.
        /// </summary>
        public void GetResult()
        {
        }
    }
}
