namespace Txfio.Tests.Stress;

public sealed class TransferOperationTests
{
    /// <summary>
    /// ファイルとディレクトリの Copy、Import、Export を含む列が、木のモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: d と e と a.txt は最初からあり、外にはファイルとディレクトリがある。ファイルの長さは空、数バイト、数十 KB、数 MB からシードが選ぶ</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose したあと RecoverAsync する</para>
    /// <para>期待: 通る手はモデルどおりに反映され、拒否される手はモデルを変えず、途中の ReadAsync と GetEntriesAsync はモデルと一致し、Commit が Succeeded ならワークフォルダはモデル、それ以外は開始前のままである。外へ書き出したものはコピー時点の内容のまま、破棄のあとにも RecoverAsync のあとにも残る</para>
    /// </remarks>
    [Fact]
    public async Task コピーと取り込みと書き出しの列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            TransferScenario scenario = TransferScenario.Generate(baseSeed + i, maxOperations: 10, maxBytes);
            string? failure = await TransferRunner.RunAsync(scenario);
            if (failure is not null)
            {
                (TransferScenario shrunk, string shrunkFailure) = await TransferRunner.ShrinkAsync(scenario, failure);
                Assert.Fail(
                    StressSettings.SeedVariable + "=" + scenario.Seed + " で約束が破れた（縮めた列）" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// 同じシードは、ファイルとディレクトリの Copy、Import、Export を含む同じ列を作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB。外にはファイル in-file とディレクトリ in-dir がある</para>
    /// <para>手順: 同じシードで列を 2 つ作り、別のシードも多数見る</para>
    /// <para>期待: 2 つの列は一致し、どの列にもファイルとディレクトリの Copy、Import、Export がある</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードはコピーと取り込みと書き出しを含む同じ列を作ること()
    {
        TransferScenario left = TransferScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        TransferScenario right = TransferScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        Assert.Equal(left.Describe(), right.Describe());
        foreach (string file in left.Outside.Files)
        {
            Assert.True(left.Outside.File(file).AsSpan().SequenceEqual(right.Outside.File(file)));
        }

        for (int seed = 0; seed < 40; seed++)
        {
            TransferScenario scenario = TransferScenario.Generate(seed, 10, StressContent.DefaultMaxBytes);
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Copy && operation.Destination == "g");
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Copy && operation.Destination == "c.txt");
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Import && operation.Destination == "h");
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Import && operation.Destination == "b.txt");
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Export && operation.Source == "d");
            Assert.Contains(scenario.Operations, operation => operation.Kind == TransferKind.Export && operation.Source == "b.txt");
        }
    }
}
