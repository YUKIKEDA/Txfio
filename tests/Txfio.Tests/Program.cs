namespace Txfio.Tests;

/// <summary>
/// 別プロセスのロック確認と耐久テスト用の入口
/// </summary>
public static class Program
{
    /// <summary>
    /// 引数があるときだけ、子プロセスとしてロックを持ち続けるか、耐久テストのトランザクションを繰り返す
    /// </summary>
    /// <param name="args"><c>dotnet test</c> からは呼ばれない起動引数</param>
    /// <returns>終了コード</returns>
    public static Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Task.FromResult(0);
        }

        return args[0] switch
        {
            ProcessLockChild.HoldUntilStop => ProcessLockChild.RunHoldUntilStopAsync(args),
            ProcessLockChild.HoldUntilKilled => ProcessLockChild.RunHoldUntilKilledAsync(args),
            Stress.StressWriter.Command => Stress.StressWriter.RunAsync(args),
            _ => Task.FromResult(2),
        };
    }
}
