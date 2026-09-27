using System.Globalization;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

public sealed class CrashStressTests
{
    /// <summary>
    /// Even when the child is killed during large Add / Update and ZIP operations, after Recover the disk matches the log or one step past it.
    /// </summary>
    /// <remarks>
    /// <para>Given: the child repeats 4 MiB Add, Update, ZIP create, and extract in numbered order. Only numbers whose commit succeeded are logged, and the number about to start is written to a separate file. The product code has no exit points.</para>
    /// <para>When: after the child writes the first number, the parent waits a little, kills the process, and runs RecoverAsync.</para>
    /// <para>Then: no journal remains, and the disk matches either up to the logged number or the log plus the next one.</para>
    /// </remarks>
    [Fact]
    public async Task KillingChildDuringCommit_DiskMatchesLogOrOneStepPastAfterRecover()
    {
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        int transactions = 8;
        await using TempDirectory temp = TempDirectory.Create();
        string work = System.IO.Path.Combine(temp.Path, "work");
        string logs = System.IO.Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(logs);
        string logFile = System.IO.Path.Combine(logs, "done.log");
        string intentFile = System.IO.Path.Combine(logs, "intent.txt");
        string startFile = System.IO.Path.Combine(logs, "start");
        StressProcess child = StressProcess.StartCrash(work, transactions, maxBytes, logFile, intentFile, startFile);
        try
        {
            await File.WriteAllTextAsync(startFile, "start");
            await WaitForIntentAsync(intentFile);
            await Task.Delay(300);
            await child.KillAsync();
        }
        finally
        {
            await child.DisposeAsync();
        }

        await global::Txfio.Txfio.RecoverAsync(work);
        string metadata = System.IO.Path.Combine(work, ".txfio");
        if (Directory.Exists(metadata))
        {
            Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        }

        List<int> logged = await ReadLogAsync(logFile);
        int? intent = await ReadIntentAsync(intentFile);
        string? matched = await MatchesAsync(work, logged, maxBytes);
        if (matched is not null && intent is int next && !logged.Contains(next))
        {
            List<int> ahead = new List<int>(logged);
            ahead.Add(next);
            matched = await MatchesAsync(work, ahead, maxBytes);
        }

        Assert.True(matched is null, matched + " log [" + string.Join(",", logged) + "] intent " + (intent?.ToString(CultureInfo.InvariantCulture) ?? "none"));
    }

    private static async Task WaitForIntentAsync(string intentFile)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(intentFile))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<List<int>> ReadLogAsync(string logFile)
    {
        List<int> indexes = new List<int>();
        if (!File.Exists(logFile))
        {
            return indexes;
        }

        foreach (string line in await File.ReadAllLinesAsync(logFile))
        {
            if (line.Length > 0)
            {
                indexes.Add(int.Parse(line, CultureInfo.InvariantCulture));
            }
        }

        return indexes;
    }

    private static async Task<int?> ReadIntentAsync(string intentFile)
    {
        if (!File.Exists(intentFile))
        {
            return null;
        }

        string text = (await File.ReadAllTextAsync(intentFile)).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> MatchesAsync(string actualWork, IReadOnlyList<int> indexes, int maxBytes)
    {
        await using TempDirectory replayRoot = TempDirectory.Create();
        string replayWork = System.IO.Path.Combine(replayRoot.Path, "work");
        Directory.CreateDirectory(replayWork);
        foreach (int index in indexes)
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(replayWork);
            await CrashStressWriter.ApplyAsync(tx, replayWork, index, maxBytes);
            CommitReport report = await tx.CommitAsync();
            if (report.Result != CommitResult.Succeeded)
            {
                return "Replay " + index + " is " + report.Result;
            }
        }

        return await CompareAsync(actualWork, replayWork);
    }

    private static async Task<string?> CompareAsync(string actualRoot, string expectedRoot)
    {
        DirectoryTree actual = await ReadTreeAsync(actualRoot);
        DirectoryTree expected = await ReadTreeAsync(expectedRoot);
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

    private static async Task<DirectoryTree> ReadTreeAsync(string root)
    {
        DirectoryTree tree = new DirectoryTree();
        if (!Directory.Exists(root))
        {
            return tree;
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, directory).Replace('\\', '/');
            if (!relative.StartsWith(".txfio", StringComparison.Ordinal))
            {
                tree.AddDirectory(relative);
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                tree.PutFile(relative, await File.ReadAllBytesAsync(file));
            }
        }

        return tree;
    }
}
