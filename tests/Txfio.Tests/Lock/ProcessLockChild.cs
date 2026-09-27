using Txfio.Tests.Support;

namespace Txfio.Tests;

/// <summary>
/// Holds the lock on a given path in another process started by a test.
/// </summary>
public static class ProcessLockChild
{
    /// <summary>
    /// The command that holds the lock until a stop file appears.
    /// </summary>
    public const string HoldUntilStop = "hold-until-stop";

    /// <summary>
    /// The command that holds the lock until the process exits.
    /// </summary>
    public const string HoldUntilKilled = "hold-until-killed";

    /// <summary>
    /// After an Add, waits while holding the lock until a stop file appears.
    /// </summary>
    /// <param name="args">The command, work folder, relative path, ready file, and stop file.</param>
    /// <returns>0 if all arguments are given, 2 if some are missing.</returns>
    public static async Task<int> RunHoldUntilStopAsync(string[] args)
    {
        if (args.Length != 5)
        {
            return 2;
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(args[1]);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("held");
        await tx.AddAsync(args[2], content);
        await File.WriteAllTextAsync(args[3], "ready");
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(args[4]))
        {
            await Task.Delay(20, timeout.Token);
        }

        return 0;
    }

    /// <summary>
    /// After an Add, waits while holding the lock until the process exits.
    /// </summary>
    /// <param name="args">The command, work folder, relative path, and ready file.</param>
    /// <returns>0 if all arguments are given, 2 if some are missing.</returns>
    public static async Task<int> RunHoldUntilKilledAsync(string[] args)
    {
        if (args.Length != 4)
        {
            return 2;
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(args[1]);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("held");
        await tx.AddAsync(args[2], content);
        await File.WriteAllTextAsync(args[3], "ready");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
