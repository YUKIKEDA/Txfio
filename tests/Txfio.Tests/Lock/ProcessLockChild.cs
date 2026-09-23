using Txfio.Tests.Support;

namespace Txfio.Tests;

/// <summary>
/// テストが起動する別プロセスで、指定パスのロックを持ち続ける
/// </summary>
public static class ProcessLockChild
{
    /// <summary>
    /// 停止ファイルができるまでロックを持つ起動コマンド
    /// </summary>
    public const string HoldUntilStop = "hold-until-stop";

    /// <summary>
    /// プロセスが終了するまでロックを持つ起動コマンド
    /// </summary>
    public const string HoldUntilKilled = "hold-until-killed";

    /// <summary>
    /// Add したあと、停止ファイルができるまでロックを持ったまま待つ
    /// </summary>
    /// <param name="args">コマンド、ワークフォルダ、相対パス、準備ファイル、停止ファイル</param>
    /// <returns>引数が揃っていれば 0、足りなければ 2</returns>
    public static async Task<int> RunHoldUntilStopAsync(string[] args)
    {
        if (args.Length != 5)
        {
            return 2;
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(args[1]);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("held");
        await tx.AddAsync(args[2], content);
        await File.WriteAllTextAsync(args[3], "ready");
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(args[4]))
        {
            await Task.Delay(20, timeout.Token);
        }

        return 0;
    }

    /// <summary>
    /// Add したあと、プロセスが終了するまでロックを持ったまま待つ
    /// </summary>
    /// <param name="args">コマンド、ワークフォルダ、相対パス、準備ファイル</param>
    /// <returns>引数が揃っていれば 0、足りなければ 2</returns>
    public static async Task<int> RunHoldUntilKilledAsync(string[] args)
    {
        if (args.Length != 4)
        {
            return 2;
        }

        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(args[1]);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("held");
        await tx.AddAsync(args[2], content);
        await File.WriteAllTextAsync(args[3], "ready");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
