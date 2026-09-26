using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 親に殺される子プロセスで、大きい Add / Update と ZIP の作成・展開を繰り返す
/// </summary>
public static class CrashStressWriter
{
    /// <summary>
    /// 起動コマンド
    /// </summary>
    public const string Command = "stress-crash-writer";

    private static readonly DateTime _archiveTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 開始ファイルができたら、番号順に大きいトランザクションをコミットし、成功した番号だけを記録する
    /// </summary>
    /// <remarks>
    /// 始める直前に意図ファイルへ番号を書く。コミットが成功したあとで記録へ同じ番号を書く。
    /// 殺された時刻によっては、意図ファイルの番号が記録より 1 つ先になる
    /// </remarks>
    /// <param name="args">コマンド、ワークフォルダ、トランザクション数、長さの上限、記録ファイル、意図ファイル、開始ファイル</param>
    /// <returns>最後まで回れば 0、引数が足りなければ 2、想定外の例外なら 1</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 7)
        {
            return 2;
        }

        string workFolder = args[1];
        int transactions = int.Parse(args[2], CultureInfo.InvariantCulture);
        int maxBytes = int.Parse(args[3], CultureInfo.InvariantCulture);
        string logFile = args[4];
        string intentFile = args[5];
        string startFile = args[6];
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(startFile))
            {
                await Task.Delay(10, timeout.Token);
            }

            await using StreamWriter log = new StreamWriter(logFile, append: false, new UTF8Encoding(false));
            for (int index = 0; index < transactions; index++)
            {
                await File.WriteAllTextAsync(intentFile, index.ToString(CultureInfo.InvariantCulture));
                await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
                await ApplyAsync(tx, workFolder, index, maxBytes);
                await tx.CommitAsync();
                await log.WriteLineAsync(index.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>
    /// 番号のトランザクションを 1 つステージする。0 から Add、Update、ZIP の作成、展開を繰り返す
    /// </summary>
    /// <param name="tx">開いているトランザクション</param>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="index">0 から始まる番号</param>
    /// <param name="maxBytes">ファイルの長さ。1 MiB 以上ならその長さ、それ未満ならその上限</param>
    /// <returns>ステージングの完了</returns>
    public static async Task ApplyAsync(ITransaction tx, string workFolder, int index, int maxBytes)
    {
        int generation = index / 4;
        int step = index % 4;
        string bin = "g" + generation.ToString(CultureInfo.InvariantCulture) + ".bin";
        string zip = "g" + generation.ToString(CultureInfo.InvariantCulture) + ".zip";
        string directory = "g" + generation.ToString(CultureInfo.InvariantCulture) + "-out";
        if (step == 0)
        {
            await using MemoryStream added = new MemoryStream(Payload(index, maxBytes));
            await tx.AddAsync(bin, added);
            return;
        }

        if (step == 1)
        {
            await using MemoryStream updated = new MemoryStream(Payload(index, maxBytes));
            await tx.UpdateAsync(bin, updated);
            return;
        }

        if (step == 2)
        {
            File.SetLastWriteTimeUtc(Full(workFolder, bin), _archiveTime);
            await tx.CreateArchiveAsync(bin, zip);
            return;
        }

        await tx.ExtractArchiveAsync(zip, directory);
    }

    /// <summary>
    /// クラッシュ耐久が書くファイルの長さ
    /// </summary>
    /// <param name="maxBytes">上限</param>
    /// <returns>バイト数</returns>
    public static int PayloadLength(int maxBytes)
    {
        const int OneMegabyte = 1024 * 1024;
        if (maxBytes >= OneMegabyte)
        {
            return maxBytes;
        }

        return Math.Max(maxBytes, 0);
    }

    private static byte[] Payload(int index, int maxBytes)
    {
        int length = PayloadLength(maxBytes);
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(index + i);
        }

        return bytes;
    }

    private static string Full(string workFolder, string path)
    {
        return System.IO.Path.Combine(workFolder, path);
    }
}
