using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// In a child process started by the multi-process stress test, repeats transactions on shared directories.
/// </summary>
public static class DirectoryStressWriter
{
    /// <summary>
    /// The start-up command.
    /// </summary>
    public const string Command = "stress-dir-writer";

    /// <summary>
    /// The relative paths of the directories the child processes compete for.
    /// </summary>
    /// <param name="count">The number of directories.</param>
    /// <returns>The paths in order from <c>d0</c>.</returns>
    public static IReadOnlyList<string> Paths(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"d{index}"))
            .ToArray();
    }

    /// <summary>
    /// Once the start file appears, repeats directory create, delete of an empty directory, delete of a tree, and Move without overwrite.
    /// </summary>
    /// <remarks>
    /// Lines are tab-separated: token, result, time just before Commit, number of attempts, and operation.
    /// An operation is <c>path=kind</c>, and a Move is <c>path&gt;destination=Move</c>.
    /// A retrying child waits a little after lock contention, looks at the disk at that time, and retries.
    /// </remarks>
    /// <param name="args">The command, work folder, child number, transaction count, seed, directory count, whether to retry (1 or 0), log file, and start file.</param>
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
        IReadOnlyList<string> paths,
        bool retry,
        string token,
        Random random)
    {
        bool dispose = random.Next(5) == 0;
        int attempts = 0;
        while (true)
        {
            attempts++;
            (string outcome, long timestamp, string operation) = await TryOnceAsync(workFolder, paths, dispose, random);
            bool again = retry && outcome == nameof(LockContentionException) && attempts < StressWriter.MaxAttempts;
            if (!again)
            {
                return string.Join(
                    '\t',
                    token,
                    outcome,
                    timestamp.ToString(CultureInfo.InvariantCulture),
                    attempts.ToString(CultureInfo.InvariantCulture),
                    operation);
            }

            await Task.Delay(random.Next(1, 1 + (1 << Math.Min(attempts, 5))));
        }
    }

    private static async Task<(string Outcome, long Timestamp, string Operation)> TryOnceAsync(
        string workFolder,
        IReadOnlyList<string> paths,
        bool dispose,
        Random random)
    {
        string path = paths[random.Next(paths.Count)];
        string full = Full(workFolder, path);
        string operation;
        string outcome;
        long timestamp = 0;
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
            if (!Directory.Exists(full))
            {
                await tx.CreateDirectoryAsync(path);
                operation = path + "=Create";
            }
            else if (IsEmpty(full))
            {
                operation = random.Next(2) == 0
                    ? await DeleteAsync(tx, path)
                    : await MoveOrDeleteAsync(tx, workFolder, paths, path, empty: true, random);
            }
            else
            {
                operation = random.Next(2) == 0
                    ? await DeleteTreeAsync(tx, path)
                    : await MoveOrDeleteAsync(tx, workFolder, paths, path, empty: false, random);
            }

            if (dispose)
            {
                outcome = "Disposed";
            }
            else
            {
                timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                outcome = (await tx.CommitAsync()).Result.ToString();
            }
        }
        catch (LockContentionException)
        {
            outcome = nameof(LockContentionException);
            operation = string.Empty;
        }
        catch (ExternalConflictException)
        {
            outcome = nameof(ExternalConflictException);
            operation = string.Empty;
        }
        catch (InvalidOperationException)
        {
            outcome = nameof(InvalidOperationException);
            operation = string.Empty;
        }

        return (outcome, timestamp, operation);
    }

    private static async Task<string> DeleteAsync(ITransaction tx, string path)
    {
        await tx.DeleteAsync(path);
        return path + "=Delete";
    }

    private static async Task<string> DeleteTreeAsync(ITransaction tx, string path)
    {
        await tx.DeleteTreeAsync(path);
        return path + "=DeleteTree";
    }

    private static async Task<string> MoveOrDeleteAsync(
        ITransaction tx,
        string workFolder,
        IReadOnlyList<string> paths,
        string path,
        bool empty,
        Random random)
    {
        List<string> free = new List<string>();
        foreach (string candidate in paths)
        {
            if (candidate != path && !Directory.Exists(Full(workFolder, candidate)))
            {
                free.Add(candidate);
            }
        }

        if (free.Count == 0)
        {
            return empty ? await DeleteAsync(tx, path) : await DeleteTreeAsync(tx, path);
        }

        string destination = free[random.Next(free.Count)];
        await tx.MoveAsync(path, destination);
        return path + ">" + destination + "=Move";
    }

    private static bool IsEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }

    private static string Full(string workFolder, string path)
    {
        return System.IO.Path.Combine(workFolder, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
