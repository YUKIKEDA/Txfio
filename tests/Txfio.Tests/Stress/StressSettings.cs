using System.Globalization;

namespace Txfio.Tests.Stress;

/// <summary>
/// 耐久テストの規模を環境変数から読む
/// </summary>
/// <remarks>
/// 既定は通常の <c>dotnet test</c> で短く終わる小ささにする。手元で長く回すときだけ環境変数で大きくする
/// </remarks>
internal static class StressSettings
{
    /// <summary>
    /// 乱数の元になるシード
    /// </summary>
    public const string SeedVariable = "TXFIO_STRESS_SEED";

    /// <summary>
    /// ランダム操作列の本数、または子プロセスごとのトランザクション数
    /// </summary>
    public const string IterationsVariable = "TXFIO_STRESS_ITERATIONS";

    /// <summary>
    /// 同時に動かす子プロセスの数
    /// </summary>
    public const string ProcessesVariable = "TXFIO_STRESS_PROCESSES";

    /// <summary>
    /// 環境変数のシード。無ければ既定値
    /// </summary>
    /// <param name="defaultValue">環境変数が無いときの値</param>
    /// <returns>シード</returns>
    public static int Seed(int defaultValue) => Read(SeedVariable, defaultValue);

    /// <summary>
    /// 環境変数の回数。無ければ既定値
    /// </summary>
    /// <param name="defaultValue">環境変数が無いときの値</param>
    /// <returns>回数</returns>
    public static int Iterations(int defaultValue) => Read(IterationsVariable, defaultValue);

    /// <summary>
    /// 環境変数のプロセス数。無ければ既定値
    /// </summary>
    /// <param name="defaultValue">環境変数が無いときの値</param>
    /// <returns>プロセス数</returns>
    public static int Processes(int defaultValue) => Read(ProcessesVariable, defaultValue);

    private static int Read(string name, int defaultValue)
    {
        string? text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
