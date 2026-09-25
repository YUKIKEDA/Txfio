using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 多プロセスの耐久テストが起動する子プロセスで、共有のファイルへトランザクションを繰り返す
/// </summary>
public static class StressWriter
{
    /// <summary>
    /// 起動コマンド
    /// </summary>
    public const string Command = "stress-writer";

    /// <summary>
    /// リトライする子が、ロック競合で 1 つのトランザクションを試す回数の上限
    /// </summary>
    public const int MaxAttempts = 50;

    /// <summary>
    /// 子プロセスが奪い合うファイルの相対パス
    /// </summary>
    /// <param name="count">ファイルの数</param>
    /// <returns><c>f0.txt</c> から順に並べたパス</returns>
    public static IReadOnlyList<string> Paths(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"f{index}.txt"))
            .ToArray();
    }

    /// <summary>
    /// 開始ファイルができたら、トランザクションを決まった数だけ繰り返し、1 件ごとに記録ファイルへ 1 行書く
    /// </summary>
    /// <remarks>
    /// 行はタブ区切りで、トークン、結果、Commit 直前の時刻、試した回数、操作（<c>パス=種類</c> をセミコロンで連結）である。
    /// 時刻はパスのロックを持ったまま取るので、同じパスに触れたトランザクションのあいだでは適用の順に並ぶ。
    /// リトライする子は、ロック競合のあと少し待って同じパスの組をやり直す。Add / Update / Delete はやり直すたびにディスクを見て決め直す
    /// </remarks>
    /// <param name="args">コマンド、ワークフォルダ、子の番号、トランザクション数、シード、ファイル数、リトライするか（1 か 0）、記録ファイル、開始ファイル</param>
    /// <returns>最後まで回れば 0、引数が足りなければ 2、想定外の例外なら 1</returns>
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
