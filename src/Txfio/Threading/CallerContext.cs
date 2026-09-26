using System.Runtime.CompilerServices;

namespace Txfio;

/// <summary>
/// 公開の非同期メソッドが、呼び出し元のスレッド（UI など）で IO をしないようにする
/// </summary>
internal static class CallerContext
{
    /// <summary>
    /// 呼び出し元に <see cref="SynchronizationContext"/> か既定以外の <see cref="TaskScheduler"/> があれば、スレッドプールへ移る
    /// </summary>
    /// <remarks>
    /// どちらも無ければ（サーバー、コンソール、既にスレッドプール上）移らずにそのまま続ける
    /// </remarks>
    /// <returns>await するとスレッドプールで続く値</returns>
    internal static LeaveAwaitable LeaveAsync()
    {
        return default;
    }

    /// <summary>
    /// <see cref="LeaveAsync"/> の await 先
    /// </summary>
    internal readonly struct LeaveAwaitable : ICriticalNotifyCompletion
    {
        /// <summary>
        /// 移る必要が無ければ <see langword="true"/>
        /// </summary>
        public bool IsCompleted => SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;

        /// <summary>
        /// await のための自分自身
        /// </summary>
        /// <returns>この値</returns>
        public LeaveAwaitable GetAwaiter()
        {
            return this;
        }

        /// <summary>
        /// 続きをスレッドプールで動かす
        /// </summary>
        /// <param name="continuation">続き</param>
        public void OnCompleted(Action continuation)
        {
            ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);
        }

        /// <summary>
        /// 続きを、実行コンテキストを流さずにスレッドプールで動かす
        /// </summary>
        /// <param name="continuation">続き</param>
        public void UnsafeOnCompleted(Action continuation)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), continuation);
        }

        /// <summary>
        /// 移ったあとは何も返さない
        /// </summary>
        public void GetResult()
        {
        }
    }
}
