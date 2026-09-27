using System.Globalization;
using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// In a child process that the parent kills, repeats large Add / Update and ZIP create and extract.
/// </summary>
public static class CrashStressWriter
{
    /// <summary>
    /// The start-up command.
    /// </summary>
    public const string Command = "stress-crash-writer";

    private static readonly DateTime _archiveTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Once the start file appears, commits large transactions in numbered order, and logs only the numbers that succeeded.
    /// </summary>
    /// <remarks>
    /// Just before starting, writes the number to the intent file. After the commit succeeds, writes the same number to the log.
    /// Depending on when it is killed, the intent file's number can be one past the log.
    /// </remarks>
    /// <param name="args">The command, work folder, transaction count, length limit, log file, intent file, and start file.</param>
    /// <returns>0 if it runs to the end, 2 if arguments are missing, 1 on an unexpected exception.</returns>
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
    /// Stages the transaction for a number. From 0 it cycles through Add, Update, ZIP create, and extract.
    /// </summary>
    /// <param name="tx">The open transaction.</param>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="index">The zero-based number.</param>
    /// <param name="maxBytes">The file length. At least 1 MiB means that length; less means the limit.</param>
    /// <returns>A task that completes when the content is staged.</returns>
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
    /// The length of the files the crash stress test writes.
    /// </summary>
    /// <param name="maxBytes">The limit.</param>
    /// <returns>The number of bytes.</returns>
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
