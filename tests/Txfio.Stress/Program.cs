namespace Txfio.Tests;

/// <summary>
/// 耐久テストの子プロセスの入口
/// </summary>
public static class Program
{
    /// <summary>
    /// 引数があるときだけ、子プロセスとして耐久テストのトランザクションを繰り返す
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
            Stress.StressWriter.Command => Stress.StressWriter.RunAsync(args),
            _ => Task.FromResult(2),
        };
    }
}
