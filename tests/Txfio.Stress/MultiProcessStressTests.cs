using System.Globalization;
using System.Text;
using Txfio.Tests.Support;
using Xunit.Abstractions;

namespace Txfio.Tests.Stress;

public sealed class MultiProcessStressTests
{
    private const string Initial = "init";

    private readonly ITestOutputHelper _output;

    public MultiProcessStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Even when several processes compete for a few files without waiting, only the result of the last committed transaction remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: of four files, only f0 and f1 exist. The process count, transaction count, file count, and seed can be changed with environment variables.</para>
    /// <para>When: child processes run at the same time, each repeating Add / Update / Delete on 1 to 3 paths with Commit or Dispose. Lock contention is not retried.</para>
    /// <para>Then: failures are only LockContentionException, ExternalConflictException, or Failed; each path has the result of the last Succeeded by the time just before Commit; no .txnew or journal remains; and RecoverAsync does nothing.</para>
    /// </remarks>
    [Fact]
    public async Task MultiProcessConcurrentUpdates_OnlyLastCommitRemains()
    {
        await RunAndVerifyAsync(StressSettings.Files(4), retry: false);
    }

    /// <summary>
    /// Even when several processes update more files with retries, only the result of the last committed transaction remains.
    /// </summary>
    /// <remarks>
    /// <para>Given: of 16 files, only f0 and f1 exist. The process count, transaction count, file count, and seed can be changed with environment variables.</para>
    /// <para>When: child processes run at the same time; on lock contention they wait a little and retry the same set of paths (up to a limit).</para>
    /// <para>Then: the same promises as without waiting hold, and no transaction stays in contention up to the limit.</para>
    /// </remarks>
    [Fact]
    public async Task MultiProcessConcurrentUpdatesWithRetry_OnlyLastCommitRemains()
    {
        await RunAndVerifyAsync(StressSettings.Files(16), retry: true);
    }

    private static SortedDictionary<string, string> ExpectedFiles(List<StressRecord> records)
    {
        SortedDictionary<string, string> expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["f0.txt"] = Initial,
            ["f1.txt"] = Initial,
        };
        IEnumerable<(StressRecord Record, string Path, string Kind)> applied = records
            .Where(record => record.Outcome == nameof(CommitResult.Succeeded))
            .OrderBy(record => record.Timestamp)
            .SelectMany(record => record.Operations.Select(operation => (record, operation.Path, operation.Kind)));
        foreach ((StressRecord record, string path, string kind) in applied)
        {
            if (kind == "Delete")
            {
                expected.Remove(path);
            }
            else
            {
                expected[path] = record.Token;
            }
        }

        return expected;
    }

    private static string Format(SortedDictionary<string, string> files)
    {
        StringBuilder text = new StringBuilder();
        foreach (KeyValuePair<string, string> file in files)
        {
            text.Append(file.Key).Append("=\"").Append(file.Value).Append("\" ");
        }

        return text.ToString().TrimEnd();
    }

    private async Task RunAndVerifyAsync(int files, bool retry)
    {
        int seed = StressSettings.Seed(1);
        int processes = StressSettings.Processes(3);
        int transactions = StressSettings.Iterations(15);
        Assert.True(files >= 2, StressSettings.FilesVariable + " must be 2 or more");
        await using TempDirectory temp = TempDirectory.Create();
        string work = System.IO.Path.Combine(temp.Path, "work");
        string logs = System.IO.Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "f0.txt"), Initial);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "f1.txt"), Initial);
        string startFile = System.IO.Path.Combine(logs, "start");

        List<StressProcess> children = new List<StressProcess>();
        try
        {
            for (int child = 0; child < processes; child++)
            {
                string logFile = System.IO.Path.Combine(logs, child.ToString(CultureInfo.InvariantCulture) + ".log");
                children.Add(StressProcess.Start(work, child, transactions, (seed * 1000) + child, files, retry, logFile, startFile));
            }

            await File.WriteAllTextAsync(startFile, "start");
            foreach (StressProcess child in children)
            {
                await child.WaitForSuccessAsync(TimeSpan.FromMinutes(10));
            }
        }
        finally
        {
            foreach (StressProcess child in children)
            {
                await child.DisposeAsync();
            }
        }

        List<StressRecord> records = new List<StressRecord>();
        foreach (string logFile in Directory.EnumerateFiles(logs, "*.log"))
        {
            foreach (string line in await File.ReadAllLinesAsync(logFile))
            {
                records.Add(StressRecord.Parse(line));
            }
        }

        string summary = $"{StressSettings.SeedVariable}={seed} files={files} retry={retry} attempts={records.Sum(record => record.Attempts)} "
            + string.Join(", ", records.GroupBy(record => record.Outcome).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + "=" + group.Count()));
        _output.WriteLine(summary);
        Assert.Equal(processes * transactions, records.Count);
        string[] allowed = { nameof(CommitResult.Succeeded), nameof(CommitResult.Failed), "Disposed", nameof(LockContentionException), nameof(ExternalConflictException) };
        StressRecord? unexpected = records.FirstOrDefault(record => !allowed.Contains(record.Outcome));
        Assert.True(unexpected is null, $"Unexpected result {unexpected} ({summary})");
        Assert.True(records.Any(record => record.Outcome == nameof(CommitResult.Succeeded)), "No Succeeded at all (" + summary + ")");
        if (retry)
        {
            Assert.True(
                records.All(record => record.Outcome != nameof(LockContentionException)),
                $"{StressWriter.MaxAttempts} retries still left a transaction in contention ({summary})");
        }

        SortedDictionary<string, string> expected = ExpectedFiles(records);
        SortedDictionary<string, string> actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(work, file).Replace('\\', '/');
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                actual[relative] = await File.ReadAllTextAsync(file);
            }
        }

        Assert.True(
            Format(expected) == Format(actual),
            $"The final state differs ({summary}){Environment.NewLine}expected [{Format(expected)}]{Environment.NewLine}actual [{Format(actual)}]");
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work, ".txfio"), "tx-*.journal"));
        Assert.Equal(RecoverResult.NoPendingTransactions, (await global::Txfio.Txfio.RecoverAsync(work)).Result);
    }

    private sealed record StressRecord(
        string Token,
        string Outcome,
        long Timestamp,
        int Attempts,
        IReadOnlyList<(string Path, string Kind)> Operations)
    {
        public static StressRecord Parse(string line)
        {
            string[] fields = line.Split('\t');
            List<(string Path, string Kind)> operations = fields[4]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(operation => operation.Split('='))
                .Select(parts => (parts[0], parts[1]))
                .ToList();
            return new StressRecord(
                fields[0],
                fields[1],
                long.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[3], CultureInfo.InvariantCulture),
                operations);
        }

        public override string ToString() => $"{Token} {Outcome}";
    }
}
