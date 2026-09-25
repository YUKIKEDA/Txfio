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
    /// 子プロセスが奪い合うファイル
    /// </summary>
    public static readonly IReadOnlyList<string> Paths = new[] { "f0.txt", "f1.txt", "f2.txt", "f3.txt" };

    /// <summary>
    /// 開始ファイルができたら、トランザクションを決まった数だけ繰り返し、1 件ごとに記録ファイルへ 1 行書く
    /// </summary>
    /// <remarks>
    /// 行はタブ区切りで、トークン、結果、Commit 直前の時刻、操作（<c>パス=種類</c> をセミコロンで連結）である。
    /// 時刻はパスのロックを持ったまま取るので、同じパスに触れたトランザクションのあいだでは適用の順に並ぶ
    /// </remarks>
    /// <param name="args">コマンド、ワークフォルダ、子の番号、トランザクション数、シード、記録ファイル、開始ファイル</param>
    /// <returns>最後まで回れば 0、引数が足りなければ 2、想定外の例外なら 1</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 7)
        {
            return 2;
        }

        string workFolder = args[1];
        int child = int.Parse(args[2], CultureInfo.InvariantCulture);
        int transactions = int.Parse(args[3], CultureInfo.InvariantCulture);
        Random random = new Random(int.Parse(args[4], CultureInfo.InvariantCulture));
        string logFile = args[5];
        string startFile = args[6];
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
                string line = await RunOneAsync(workFolder, token, random);
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

    private static async Task<string> RunOneAsync(string workFolder, string token, Random random)
    {
        List<string> paths = Paths.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
        bool dispose = random.Next(5) == 0;
        List<string> operations = new List<string>();
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
                outcome = (await tx.CommitAsync()).ToString();
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

        return string.Join(
            '\t',
            token,
            outcome,
            timestamp.ToString(CultureInfo.InvariantCulture),
            string.Join(';', operations));
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
