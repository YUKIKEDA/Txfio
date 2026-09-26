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
    /// 少数のディレクトリを複数プロセスが待たずに奪い合っても、成功した記録の再生とディスクが一致する
    /// </summary>
    /// <remarks>
    /// <para>前提: d0 には長さの帯から選んだ a.bin があり、d1 は空である。プロセス数、トランザクション数、ディレクトリ数、シードは環境変数で変えられる</para>
    /// <para>手順: 子プロセスを同時に走らせ、ディレクトリの作成、空の削除、木の削除、上書きしない Move を Commit か Dispose で繰り返す。ロック競合はやり直さない</para>
    /// <para>期待: 失敗は競合か破棄か Failed だけでディスクに残らず、Succeeded を時刻順に再生した木とディスクが一致し、ジャーナルは残らない</para>
    /// </remarks>
    [Fact]
    public async Task 複数プロセスのディレクトリ競合_成功した記録とディスクが一致すること()
    {
        await RunAndVerifyAsync(StressSettings.Files(4), retry: false);
    }

    /// <summary>
    /// 多めのディレクトリを複数プロセスがリトライしながら奪い合っても、成功した記録の再生とディスクが一致する
    /// </summary>
    /// <remarks>
    /// <para>前提: d0 には長さの帯から選んだ a.bin があり、d1 は空である。ディレクトリ数は待たない版より多い</para>
    /// <para>手順: 子プロセスを同時に走らせ、ロック競合なら少し待ってその時点のディスクを見てやり直す（上限あり）</para>
    /// <para>期待: 待たない場合と同じ約束が守られ、上限まで競合し続けたトランザクションは無い</para>
    /// </remarks>
    [Fact]
    public async Task 複数プロセスがリトライするディレクトリ競合_成功した記録とディスクが一致すること()
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
                return "ディレクトリが無い: " + directory;
            }
        }

        foreach (string directory in actual.Directories)
        {
            if (!expected.IsDirectory(directory))
            {
                return "ディレクトリが余分: " + directory;
            }
        }

        foreach (string file in expected.Files)
        {
            if (!actual.IsFile(file) || !expected.File(file).AsSpan().SequenceEqual(actual.File(file)))
            {
                return "ファイルが違う: " + file;
            }
        }

        foreach (string file in actual.Files)
        {
            if (!expected.IsFile(file))
            {
                return "ファイルが余分: " + file;
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
        Assert.True(directories >= 2, StressSettings.FilesVariable + " は 2 以上にする");
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
        Assert.True(unexpected is null, "想定外の結果 " + unexpected + "（" + summary + "）");
        Assert.True(records.Any(record => record.Outcome == nameof(CommitResult.Succeeded)), "Succeeded が 1 件も無い（" + summary + "）");
        if (retry)
        {
            Assert.True(
                records.All(record => record.Outcome != nameof(LockContentionException)),
                StressWriter.MaxAttempts + " 回やり直しても競合したトランザクションがある（" + summary + "）");
        }

        DirectoryTree expected = Replay(records, content);
        string? mismatch = await CompareAsync(work, expected);
        Assert.True(mismatch is null, "最終状態が違う（" + summary + "） " + mismatch);
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
