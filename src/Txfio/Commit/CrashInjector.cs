namespace Txfio;

/// <summary>
/// テストがコミット中の地点を武装し、プロセスを落とす代わりに例外で止める
/// </summary>
internal static class CrashInjector
{
    /// <summary>
    /// <c>Committing</c> を書いた直後
    /// </summary>
    internal const string AfterCommitting = "AfterCommitting";

    /// <summary>
    /// 適用順の各操作が成功した直後
    /// </summary>
    internal const string AfterApply = "AfterApply";

    private static readonly AsyncLocal<State?> _state = new AsyncLocal<State?>();

    /// <summary>
    /// 注入で止めたあとなら <see langword="true"/>（その場合 Dispose はロールバックしない）
    /// </summary>
    internal static bool ShouldSkipRollback => _state.Value is { Injected: true };

    /// <summary>
    /// 次にこの地点へ来たら止める
    /// </summary>
    /// <param name="name">止める地点</param>
    internal static void Arm(string name)
    {
        _state.Value = new State(name);
    }

    /// <summary>
    /// 武装と注入済みの印を消す
    /// </summary>
    internal static void Reset()
    {
        _state.Value = null;
    }

    /// <summary>
    /// 武装した地点なら注入済みにして例外を投げる
    /// </summary>
    /// <param name="name">今の地点</param>
    internal static void CheckPoint(string name)
    {
        State? state = _state.Value;
        if (state is null || state.Injected || !string.Equals(state.ArmedName, name, StringComparison.Ordinal))
        {
            return;
        }

        // AsyncLocal の差し替えは呼び出し元へ戻らないため、同じオブジェクトを更新する
        state.Injected = true;
        throw new CrashInjectionException(name);
    }

    private sealed class State
    {
        internal State(string armedName)
        {
            ArmedName = armedName;
        }

        internal string ArmedName { get; }

        internal bool Injected { get; set; }
    }
}
