using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// In a child process started by the multi-process stress test, repeats transactions on shared files.
/// </summary>
public static class StressWriter
{
    /// <summary>
    /// The start-up command.
    /// </summary>
    public const string Command = "stress-writer";

    /// <summary>
    /// How many times a retrying child tries one transaction under lock contention.
    /// </summary>
    public const int MaxAttempts = 50;

    /// <summary>
    /// The relative paths of the files the child processes compete for.
    /// </summary>
    /// <param name="count">The number of files.</param>
    /// <returns>The paths in order from <c>f0.txt</c>.</returns>
    public static IReadOnlyList<string> Paths(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"f{index}.txt"))
            .ToArray();
    }

    /// <summary>
    /// Once the start file appears, repeats a fixed number of transactions, writing one line per transaction to the log file.
    /// </summary>
    /// <remarks>
    /// Lines are tab-separated: token, result, time just before Commit, number of attempts, and operations (<c>path=kind</c> joined with semicolons).
    /// The time is taken while holding the path locks, so transactions that touched the same path are ordered by apply.
    /// A retrying child waits a little after lock contention and retries the same set of paths. Add / Update / Delete are decided again from the disk at each retry.
    /// </remarks>
    /// <param name="args">The command, work folder, child number, transaction count, seed, file count, whether to retry (1 or 0), log file, and start file.</param>
    /// <returns>0 if it runs to the end, 2 if arguments are missing, 1 on an unexpected exception.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 9)
        {
            return 2;
        }

        string workFolder = args[1];
        int child = int.Parse(args[2], CultureInfo.InvariantCulture);
        int transactions = int.Parse(args[3], CultureInfo.InvariantCulture);
        Random random = new Random(int.Parse(args[4], CultureInfo.InvariantCulture));
        IReadOnlyList<string> paths = Paths(int.Parse(args[5], CultureInfo.InvariantCulture));
        bool retry = args[6] == "1";
        string logFile = args[7];
        string startFile = args[8];
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(startFile))
            {
                await Task.Delay(10, timeout.Token);
            }

            await using StreamWriter log = new StreamWriter(logFile, append: false, new UTF8Encoding(false));
            for (int i = 0; i < transactions; i++)
            {
                string token = string.Create(CultureInfo.InvariantCulture, $"p{child}-t{i}");
                string line = await RunOneAsync(workFolder, paths, retry, token, random);
                await log.WriteLineAsync(line);
                await log.FlushAsync();
            }

            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            return 1;
        }
    }

    private static async Task<string> RunOneAsync(
        string workFolder,
        IReadOnlyList<string> allPaths,
        bool retry,
        string token,
        Random random)
    {
        List<string> paths = allPaths.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
        bool dispose = random.Next(5) == 0;
        int attempts = 0;
        while (true)
        {
            attempts++;
            List<string> operations = new List<string>();
            (string outcome, long timestamp) = await TryOnceAsync(workFolder, paths, dispose, token, random, operations);
            bool again = retry && outcome == nameof(LockContentionException) && attempts < MaxAttempts;
            if (!again)
            {
                return string.Join(
                    '\t',
                    token,
                    outcome,
                    timestamp.ToString(CultureInfo.InvariantCulture),
                    attempts.ToString(CultureInfo.InvariantCulture),
                    string.Join(';', operations));
            }

            await Task.Delay(random.Next(1, 1 + (1 << Math.Min(attempts, 5))));
        }
    }

    private static async Task<(string Outcome, long Timestamp)> TryOnceAsync(
        string workFolder,
        List<string> paths,
        bool dispose,
        string token,
        Random random,
        List<string> operations)
    {
        string outcome;
        long timestamp = 0;
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
            foreach (string path in paths)
            {
                operations.Add(path + "=" + await StageAsync(tx, workFolder, path, token, random));
            }

            if (dispose)
            {
                outcome = "Disposed";
            }
            else
            {
                timestamp = Stopwatch.GetTimestamp();
                outcome = (await tx.CommitAsync()).Result.ToString();
            }
        }
        catch (LockContentionException)
        {
            outcome = nameof(LockContentionException);
        }
        catch (ExternalConflictException)
        {
            outcome = nameof(ExternalConflictException);
        }

        return (outcome, timestamp);
    }

    private static async Task<string> StageAsync(ITransaction tx, string workFolder, string path, string token, Random random)
    {
        if (!File.Exists(System.IO.Path.Combine(workFolder, path)))
        {
            await using MemoryStream added = new MemoryStream(Encoding.UTF8.GetBytes(token));
            await tx.AddAsync(path, added);
            return "Add";
        }

        if (random.Next(3) == 0)
        {
            await tx.DeleteAsync(path);
            return "Delete";
        }

        await using MemoryStream updated = new MemoryStream(Encoding.UTF8.GetBytes(token));
        await tx.UpdateAsync(path, updated);
        return "Update";
    }
}
