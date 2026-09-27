using System.Globalization;
using Txfio.Tests.Support;
using Xunit.Abstractions;

namespace Txfio.Tests.Stress;

public sealed class DirectoryMultiProcessStressTests
{
    private readonly ITestOutputHelper _output;

    public DirectoryMultiProcessStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Even when several processes compete for a few directories without waiting, replaying the successful records matches the disk.
    /// </summary>
    /// <remarks>
    /// <para>Given: d0 has a.bin with a length chosen from the bands, and d1 is empty. The process count, transaction count, directory count, and seed can be changed with environment variables.</para>
    /// <para>When: child processes run at the same time, repeating directory create, delete of an empty directory, delete of a tree, and Move without overwrite, with Commit or Dispose. Lock contention is not retried.</para>
    /// <para>Then: failures are only contention, discard, or Failed and leave nothing on disk; replaying Succeeded in time order gives a tree that matches the disk; and no journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task MultiProcessDirectoryContention_RecordsMatchDisk()
    {
        await RunAndVerifyAsync(StressSettings.Files(4), retry: false);
    }

    /// <summary>
    /// Even when several processes compete for more directories with retries, replaying the successful records matches the disk.
    /// </summary>
    /// <remarks>
    /// <para>Given: d0 has a.bin with a length chosen from the bands, and d1 is empty. There are more directories than in the no-wait version.</para>
    /// <para>When: child processes run at the same time; on lock contention they wait a little, look at the disk at that time, and retry (up to a limit).</para>
    /// <para>Then: the same promises as without waiting hold, and no transaction stays in contention up to the limit.</para>
    /// </remarks>
    [Fact]
    public async Task MultiProcessDirectoryContentionWithRetry_RecordsMatchDisk()
    {
        await RunAndVerifyAsync(StressSettings.Files(16), retry: true);
    }

    private static DirectoryTree Replay(List<DirectoryStressRecord> records, byte[] content)
    {
        DirectoryTree tree = new DirectoryTree();
        tree.AddDirectory("d0");
        tree.AddDirectory("d1");
        tree.PutFile("d0/a.bin", content);
        foreach (DirectoryStressRecord record in records
            .Where(record => record.Outcome == nameof(CommitResult.Succeeded))
            .OrderBy(record => record.Timestamp))
        {
            switch (record.Kind)
            {
                case "Create":
                    tree.AddDirectory(record.Path);
                    break;
                case "Delete":
                    tree.Remove(record.Path);
                    break;
                case "DeleteTree":
                    tree.RemoveTree(record.Path);
                    break;
                default:
                    tree.Move(record.Path, record.Destination!);
                    break;
            }
        }

        return tree;
    }

    private static async Task<string?> CompareAsync(string work, DirectoryTree expected)
    {
        DirectoryTree actual = new DirectoryTree();
        foreach (string directory in Directory.EnumerateDirectories(work, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(work, directory);
            if (!relative.StartsWith(".txfio", StringComparison.Ordinal))
            {
                actual.AddDirectory(relative);
            }
        }

        foreach (string file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(work, file);
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                actual.PutFile(relative, await File.ReadAllBytesAsync(file));
            }
        }

        foreach (string directory in expected.Directories)
        {
            if (!actual.IsDirectory(directory))
            {
                return "Directory missing: " + directory;
            }
        }

        foreach (string directory in actual.Directories)
        {
            if (!expected.IsDirectory(directory))
            {
                return "Extra directory: " + directory;
            }
        }

        foreach (string file in expected.Files)
        {
            if (!actual.IsFile(file) || !expected.File(file).AsSpan().SequenceEqual(actual.File(file)))
            {
                return "File differs: " + file;
            }
        }

        foreach (string file in actual.Files)
        {
            if (!expected.IsFile(file))
            {
                return "Extra file: " + file;
            }
        }

        return null;
    }

    private static string Relative(string root, string path)
    {
        return System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private async Task RunAndVerifyAsync(int directories, bool retry)
    {
        int seed = StressSettings.Seed(1);
        int processes = StressSettings.Processes(3);
        int transactions = StressSettings.Iterations(15);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        Assert.True(directories >= 2, StressSettings.FilesVariable + " must be 2 or more");
        byte[] content = StressContent.Create(new Random(seed), maxBytes);
        await using TempDirectory temp = TempDirectory.Create();
        string work = System.IO.Path.Combine(temp.Path, "work");
        string logs = System.IO.Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(System.IO.Path.Combine(work, "d0"));
        Directory.CreateDirectory(System.IO.Path.Combine(work, "d1"));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(work, "d0", "a.bin"), content);
        string startFile = System.IO.Path.Combine(logs, "start");
        Directory.CreateDirectory(logs);

        List<StressProcess> children = new List<StressProcess>();
        try
        {
            for (int child = 0; child < processes; child++)
            {
                string logFile = System.IO.Path.Combine(logs, child.ToString(CultureInfo.InvariantCulture) + ".log");
                children.Add(StressProcess.Start(
                    work,
                    child,
                    transactions,
                    (seed * 1000) + child,
                    directories,
                    retry,
                    logFile,
                    startFile,
                    DirectoryStressWriter.Command));
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

        List<DirectoryStressRecord> records = new List<DirectoryStressRecord>();
        foreach (string logFile in Directory.EnumerateFiles(logs, "*.log"))
        {
            foreach (string line in await File.ReadAllLinesAsync(logFile))
            {
                records.Add(DirectoryStressRecord.Parse(line));
            }
        }

        string summary = StressSettings.SeedVariable + "=" + seed + " directories=" + directories + " retry=" + retry
            + " attempts=" + records.Sum(record => record.Attempts) + " "
            + string.Join(", ", records.GroupBy(record => record.Outcome).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + "=" + group.Count()));
        _output.WriteLine(summary);
        Assert.Equal(processes * transactions, records.Count);
        string[] allowed =
        {
            nameof(CommitResult.Succeeded),
            nameof(CommitResult.Failed),
            "Disposed",
            nameof(LockContentionException),
            nameof(ExternalConflictException),
            nameof(InvalidOperationException),
        };
        DirectoryStressRecord? unexpected = records.FirstOrDefault(record => !allowed.Contains(record.Outcome));
        Assert.True(unexpected is null, "Unexpected result " + unexpected + " (" + summary + ")");
        Assert.True(records.Any(record => record.Outcome == nameof(CommitResult.Succeeded)), "No Succeeded at all (" + summary + ")");
        if (retry)
        {
            Assert.True(
                records.All(record => record.Outcome != nameof(LockContentionException)),
                StressWriter.MaxAttempts + " retries still left a transaction in contention (" + summary + ")");
        }

        DirectoryTree expected = Replay(records, content);
        string? mismatch = await CompareAsync(work, expected);
        Assert.True(mismatch is null, "The final state differs (" + summary + ") " + mismatch);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work, ".txfio"), "tx-*.journal"));
        Assert.Equal(RecoverResult.NoPendingTransactions, (await global::Txfio.Txfio.RecoverAsync(work)).Result);
    }

    private sealed record DirectoryStressRecord(
        string Token,
        string Outcome,
        long Timestamp,
        int Attempts,
        string Kind,
        string Path,
        string? Destination)
    {
        public static DirectoryStressRecord Parse(string line)
        {
            string[] fields = line.Split('\t');
            string operation = fields[4];
            string kind = "None";
            string path = string.Empty;
            string? destination = null;
            if (operation.Length > 0)
            {
                int equals = operation.LastIndexOf('=');
                kind = operation.Substring(equals + 1);
                string paths = operation.Substring(0, equals);
                int arrow = paths.IndexOf('>');
                if (arrow < 0)
                {
                    path = paths;
                }
                else
                {
                    path = paths.Substring(0, arrow);
                    destination = paths.Substring(arrow + 1);
                }
            }

            return new DirectoryStressRecord(
                fields[0],
                fields[1],
                long.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[3], CultureInfo.InvariantCulture),
                kind,
                path,
                destination);
        }

        public override string ToString() => Token + " " + Outcome + " " + Kind;
    }
}
