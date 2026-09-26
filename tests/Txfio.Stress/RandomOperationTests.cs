namespace Txfio.Tests.Stress;

public sealed class RandomOperationTests
{
    /// <summary>
    /// ランダムな Add / Update / Delete / Move / Read の列が、メモリ上のモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: ルートと <c>sub</c> の下の少数のパスに、シードで決めたファイルがある。本数とシードは環境変数で変えられる</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 各手はモデルどおりに反映されるか InvalidOperationException で拒否され、途中の ReadAsync はモデルと一致し、Commit が Succeeded ならディスクはモデル、それ以外は開始前のままで、ジャーナルと .txnew は残らない</para>
    /// </remarks>
    [Fact]
    public async Task ランダムな操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        for (int i = 0; i < count; i++)
        {
            RandomOperationScenario scenario = RandomOperationScenario.Generate(baseSeed + i, maxOperations: 10);
            string? failure = await RandomOperationRunner.RunAsync(scenario);
            if (failure is not null)
            {
                (RandomOperationScenario shrunk, string shrunkFailure) = await RandomOperationRunner.ShrinkAsync(scenario, failure);
                Assert.Fail(
                    $"{StressSettings.SeedVariable}={scenario.Seed} で約束が破れた（縮めた列）{Environment.NewLine}"
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }
}
