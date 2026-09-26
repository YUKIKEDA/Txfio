namespace Txfio.Tests.Stress;

public sealed class TextOperationTests
{
    /// <summary>
    /// 文字列と JSON の読み書きを含む列が、テキストのモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: d と e と a.txt は最初からある。テキストの長さは空、数文字、数十 KB からシードが選ぶ。数 MB は長さの上限を既定より上げたときだけ使う</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 書き込みと追記のあと読み戻しはモデルと一致し、JSON は書いた値と一致し、拒否された手はモデルを変えず、Commit が Succeeded ならディスクのバイト列はモデル、それ以外は開始前のままである</para>
    /// </remarks>
    [Fact]
    public async Task 文字列とJSONの操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            TextScenario scenario = TextScenario.Generate(baseSeed + i, maxOperations: 12, maxBytes);
            string? failure = await TextRunner.RunAsync(scenario);
            if (failure is not null)
            {
                (TextScenario shrunk, string shrunkFailure) = await TextRunner.ShrinkAsync(scenario, failure);
                Assert.Fail(
                    StressSettings.SeedVariable + "=" + scenario.Seed + " で約束が破れた（縮めた列）" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// 同じシードは、文字列と JSON の各 API を含む同じ列を作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB だが、この列のテキストは数十 KB までである</para>
    /// <para>手順: 同じシードで列を 2 つ作り、別のシードも多数見る</para>
    /// <para>期待: 2 つの列は一致し、どの列にも Write、Read、Append、行、JSON の書き込みと読み戻しがある</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードは文字列とJSONを含む同じ列を作ること()
    {
        TextScenario left = TextScenario.Generate(7, 12, StressContent.DefaultMaxBytes);
        TextScenario right = TextScenario.Generate(7, 12, StressContent.DefaultMaxBytes);
        Assert.Equal(left.Describe(), right.Describe());
        foreach (string file in left.Initial.Files)
        {
            Assert.Equal(left.Initial.Text(file), right.Initial.Text(file));
        }

        for (int seed = 0; seed < 40; seed++)
        {
            TextScenario scenario = TextScenario.Generate(seed, 12, StressContent.DefaultMaxBytes);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.WriteText);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.WriteLines);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.AppendText);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.AppendLines);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.ReadText);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.ReadLines);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.WriteJson);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TextKind.ReadJson);
        }
    }
}
