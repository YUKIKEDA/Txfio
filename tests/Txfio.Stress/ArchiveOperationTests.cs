namespace Txfio.Tests.Stress;

public sealed class ArchiveOperationTests
{
    /// <summary>
    /// ZIP の作成、展開、取り込み、書き出しを含む列が、木のモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: d には a.txt と sub/b.txt と空の empty があり、直下に a.txt もある。中身の長さは空から数 MB。外には a.txt と空ディレクトリを入れた in.zip がある</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 作った ZIP を読み戻すと入れたファイルと一致し、展開と取り込みのあとのワークフォルダはモデルと一致し、拒否された手はモデルを変えず、Commit が Succeeded ならディスクはモデル、それ以外は開始前のままである。外へ書いた ZIP は破棄のあとにも残る</para>
    /// </remarks>
    [Fact]
    public async Task ZIPの操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            ArchiveScenario scenario = ArchiveScenario.Generate(baseSeed + i, maxOperations: 10, maxBytes);
            string? failure = await ArchiveRunner.RunAsync(scenario);
            if (failure is not null)
            {
                (ArchiveScenario shrunk, string shrunkFailure) = await ArchiveRunner.ShrinkAsync(scenario, failure);
                Assert.Fail(
                    StressSettings.SeedVariable + "=" + scenario.Seed + " で約束が破れた（縮めた列）" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// 同じシードは、作成、展開、取り込み、書き出しを含む同じ列を作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB。d と a.txt は最初からある</para>
    /// <para>手順: 同じシードで列を 2 つ作り、別のシードも多数見る</para>
    /// <para>期待: 2 つの列は一致し、どの列にも作成、展開、取り込み、書き出しがある</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードはZIPの列を作ること()
    {
        ArchiveScenario left = ArchiveScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        ArchiveScenario right = ArchiveScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        Assert.Equal(left.Describe(), right.Describe());
        Assert.True(left.ImportContent.AsSpan().SequenceEqual(right.ImportContent));
        for (int seed = 0; seed < 40; seed++)
        {
            ArchiveScenario scenario = ArchiveScenario.Generate(seed, 10, StressContent.DefaultMaxBytes);
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Create && operation.Source == "d");
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Extract);
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Create && operation.Source == "a.txt");
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Import);
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Export && operation.Source == "d");
            Assert.Contains(scenario.Operations, operation => operation.Kind == ArchiveKind.Export && operation.Source == "a.txt");
        }
    }
}
