namespace Txfio;

/// <summary>
/// テストが指定したコミット中の地点で、プロセス強制終了の代わりに例外を投げる
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
    /// チェックポイントで止めたあとは <see langword="true"/>（そのとき Dispose はロールバックしない）
    /// </summary>
    internal static bool ShouldSkipRollback => _state.Value is { Injected: true };

    /// <summary>
    /// 次に通過したら止める地点を指定する
    /// </summary>
    /// <param name="name">止める地点</param>
    internal static void Arm(string name)
    {
        _state.Value = new State(name);
    }

    /// <summary>
    /// 止める地点の指定を消す
    /// </summary>
    internal static void Reset()
    {
        _state.Value = null;
    }

    /// <summary>
    /// 指定した地点に達していれば、止め済みにして例外を投げる
    /// </summary>
    /// <param name="name">現在の地点</param>
    internal static void CheckPoint(string name)
    {
        State? state = _state.Value;
        if (state is null || state.Injected || !string.Equals(state.ArmedName, name, StringComparison.Ordinal))
        {
            return;
        }

        // AsyncLocal の参照を差し替えると、呼び出し元の文脈には反映されないので、同じオブジェクトを書き換える
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
