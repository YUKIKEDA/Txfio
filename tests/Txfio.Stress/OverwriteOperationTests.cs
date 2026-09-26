namespace Txfio.Tests.Stress;

public sealed class OverwriteOperationTests
{
    /// <summary>
    /// 移動先を置き換える Move を含む列が、木のモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: d と e は最初からあり、ほかのディレクトリとファイルはシードで置く。列の最初は、置けるなら置き換えの Move である</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 置き換えのあと移動先は移動元の木であり、続きで移動元と移動先を触る手は拒否されてモデルを変えず、Commit が Succeeded ならディスクはモデルで .txold は残らず、それ以外は開始前のままである</para>
    /// </remarks>
    [Fact]
    public async Task 上書きMoveの操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            DirectoryScenario scenario = OverwriteScenario.Generate(baseSeed + i, maxOperations: 10, maxBytes);
            string? failure = await DirectoryRunner.RunAsync(scenario);
            if (failure is not null)
            {
                (DirectoryScenario shrunk, string shrunkFailure) = await DirectoryRunner.ShrinkAsync(scenario, failure);
                Assert.Fail(
                    $"{StressSettings.SeedVariable}={scenario.Seed} で約束が破れた（縮めた列）{Environment.NewLine}"
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// 同じシードは置き換えを含む同じ列を作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB。d と e は最初からある</para>
    /// <para>手順: 同じシードで列を 2 つ作り、別のシードも多数見る</para>
    /// <para>期待: 2 つの列は一致し、どの列にも置き換えの Move が 1 手ある</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードは置き換えを含む同じ列を作ること()
    {
        DirectoryScenario left = OverwriteScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        DirectoryScenario right = OverwriteScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        Assert.Equal(left.Describe(), right.Describe());
        for (int seed = 0; seed < 40; seed++)
        {
            DirectoryScenario scenario = OverwriteScenario.Generate(seed, 10, StressContent.DefaultMaxBytes);
            Assert.Contains(scenario.Operations, operation => operation.Overwrite);
        }
    }
}
