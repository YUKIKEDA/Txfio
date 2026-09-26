namespace Txfio;

/// <summary>
/// テストが失敗と途中停止を仕込む
/// </summary>
internal sealed class FaultInjector : IFaultInjector
{
    private readonly Queue<(FileShare Share, Exception Exception)> _openFailures = new Queue<(FileShare Share, Exception Exception)>();

    private string? _armedName;

    private bool _injected;

    private Exception? _applyFailure;

    /// <inheritdoc />
    public bool ShouldSkipRollback => _injected;

    /// <inheritdoc />
    public void CheckPoint(string name)
    {
        if (_armedName is null || _injected || !string.Equals(_armedName, name, StringComparison.Ordinal))
        {
            return;
        }

        _injected = true;
        throw new CrashInjectionException(name);
    }

    /// <inheritdoc />
    public void ThrowIfApplyArmed()
    {
        if (_applyFailure is null)
        {
            return;
        }

        Exception exception = _applyFailure;
        _applyFailure = null;
        throw exception;
    }

    /// <inheritdoc />
    public void ThrowIfOpenArmed(FileShare share)
    {
        if (_openFailures.Count == 0 || _openFailures.Peek().Share != share)
        {
            return;
        }

        throw _openFailures.Dequeue().Exception;
    }

    /// <summary>
    /// 次に通過したら止める地点を指定する
    /// </summary>
    /// <param name="name">止める地点</param>
    internal void Arm(string name)
    {
        _armedName = name;
        _injected = false;
    }

    /// <summary>
    /// 止める地点の指定を消す
    /// </summary>
    internal void Reset()
    {
        _armedName = null;
        _injected = false;
        _applyFailure = null;
        _openFailures.Clear();
    }

    /// <summary>
    /// 次の Dispose を、ロックを閉じるだけにする
    /// </summary>
    internal void SuppressRollback()
    {
        _armedName = "suppress";
        _injected = true;
    }

    /// <summary>
    /// 次の適用で、指定した例外を投げる
    /// </summary>
    /// <param name="exception">投げる例外</param>
    internal void FailNextApply(Exception exception)
    {
        _applyFailure = exception;
    }

    /// <summary>
    /// 次に指定した共有モードで開くとき、指定した例外を投げる
    /// </summary>
    /// <param name="share">失敗させる共有モード</param>
    /// <param name="exception">投げる例外</param>
    internal void FailNextOpen(FileShare share, Exception exception)
    {
        _openFailures.Enqueue((share, exception));
    }

    /// <summary>
    /// 仕込んだ開く失敗を消す
    /// </summary>
    internal void ClearOpenFailures()
    {
        _openFailures.Clear();
    }
}
