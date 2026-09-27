namespace Txfio;

/// <summary>
/// Neither fails nor stops partway.
/// </summary>
internal sealed class NoFaultInjector : IFaultInjector
{
    /// <summary>
    /// The shared injection point that does nothing.
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
