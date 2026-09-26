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
    /// どちらも無ければ（サーバーやコンソールであるとき、既にスレッドプール上にいるとき）移らずにそのまま続ける
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
        /// await 用の自分自身
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
        /// 続きをスレッドプールで動かす（実行コンテキストは流す）
        /// </summary>
        /// <param name="continuation">続き</param>
        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        /// <summary>
        /// 何も返さない
        /// </summary>
        public void GetResult()
        {
        }
    }
}
