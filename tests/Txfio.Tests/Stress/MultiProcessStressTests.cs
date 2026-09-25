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
    /// 少数のファイルを複数プロセスが待たずに奪い合っても、最後に Commit したトランザクションの結果だけが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 4 つのファイルのうち f0 と f1 だけがある。プロセス数、トランザクション数、ファイル数、シードは環境変数で変えられる</para>
    /// <para>手順: 子プロセスを同時に走らせ、それぞれが 1〜3 パスへの Add / Update / Delete を Commit か Dispose で繰り返す。ロック競合はやり直さない</para>
    /// <para>期待: 失敗は LockContentionException か ExternalConflictException か Failed だけで、各パスは Commit 直前の時刻が最後の Succeeded の結果になり、.txnew とジャーナルは残らず、RecoverAsync は何もしない</para>
    /// </remarks>
    [Fact]
    public async Task 複数プロセスの同時更新_最後にコミットした結果だけが残ること()
    {
        await RunAndVerifyAsync(StressSettings.Files(4), retry: false);
    }

    /// <summary>
    /// 多めのファイルを複数プロセスがリトライしながら更新しても、最後に Commit したトランザクションの結果だけが残る
    /// </summary>
    /// <remarks>
    /// <para>前提: 16 のファイルのうち f0 と f1 だけがある。プロセス数、トランザクション数、ファイル数、シードは環境変数で変えられる</para>
    /// <para>手順: 子プロセスを同時に走らせ、ロック競合なら少し待って同じパスの組をやり直す（上限あり）</para>
    /// <para>期待: 待たない場合と同じ約束が守られ、上限まで競合し続けたトランザクションは無い</para>
    /// </remarks>
    [Fact]
    public async Task 複数プロセスがリトライする同時更新_最後にコミットした結果だけが残ること()
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
        Assert.True(files >= 2, StressSettings.FilesVariable + " は 2 以上にする");
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
        Assert.True(unexpected is null, $"想定外の結果 {unexpected}（{summary}）");
        Assert.True(records.Any(record => record.Outcome == nameof(CommitResult.Succeeded)), "Succeeded が 1 件も無い（" + summary + "）");
        if (retry)
        {
            Assert.True(
                records.All(record => record.Outcome != nameof(LockContentionException)),
                $"{StressWriter.MaxAttempts} 回やり直しても競合したトランザクションがある（{summary}）");
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
            $"最終状態が違う（{summary}）{Environment.NewLine}期待 [{Format(expected)}]{Environment.NewLine}実際 [{Format(actual)}]");
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
