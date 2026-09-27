namespace Txfio.Tests;

/// <summary>
/// The entry point for lock checks in another process.
/// </summary>
public static class Program
{
    /// <summary>
    /// Only when there are arguments, keeps holding locks as a child process.
    /// </summary>
    /// <param name="args">Start-up arguments (not passed from <c>dotnet test</c>).</param>
    /// <returns>The exit code.</returns>
    public static Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Task.FromResult(0);
        }

        return args[0] switch
        {
            ProcessLockChild.HoldUntilStop => ProcessLockChild.RunHoldUntilStopAsync(args),
            ProcessLockChild.HoldUntilKilled => ProcessLockChild.RunHoldUntilKilledAsync(args),
            _ => Task.FromResult(2),
        };
    }
}
