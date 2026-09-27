namespace Txfio.Tests;

/// <summary>
/// The entry point of the stress test child processes.
/// </summary>
public static class Program
{
    /// <summary>
    /// Only when there are arguments, repeats stress test transactions as a child process.
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
            Stress.StressWriter.Command => Stress.StressWriter.RunAsync(args),
            Stress.DirectoryStressWriter.Command => Stress.DirectoryStressWriter.RunAsync(args),
            Stress.CrashStressWriter.Command => Stress.CrashStressWriter.RunAsync(args),
            _ => Task.FromResult(2),
        };
    }
}
