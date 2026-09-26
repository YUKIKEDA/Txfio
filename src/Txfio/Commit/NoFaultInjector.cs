namespace Txfio;

/// <summary>
/// 失敗も途中停止もしない
/// </summary>
internal sealed class NoFaultInjector : IFaultInjector
{
    /// <summary>
    /// 何もしない共有の注入口
    /// </summary>
    public static readonly NoFaultInjector Instance = new NoFaultInjector();

    private NoFaultInjector()
    {
    }

    /// <inheritdoc />
    public bool ShouldSkipRollback => false;

    /// <inheritdoc />
    public void CheckPoint(string name)
    {
    }

    /// <inheritdoc />
    public void ThrowIfApplyArmed()
    {
    }

    /// <inheritdoc />
    public void ThrowIfOpenArmed(FileShare share)
    {
    }
}
