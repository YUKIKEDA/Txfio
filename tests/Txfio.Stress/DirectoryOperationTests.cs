namespace Txfio.Tests.Stress;

public sealed class DirectoryOperationTests
{
    /// <summary>
    /// ディレクトリの作成、削除、木の削除、上書きしない Move を含む列が、木のモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: d と e は最初からあり、ほかのディレクトリとファイルはシードで置く。ファイルの長さは空、数バイト、数十 KB、数 MB からシードが選ぶ</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 各手はモデルどおりに反映されるか InvalidOperationException か ExternalConflictException で拒否され、途中の ReadAsync と GetEntriesAsync はモデルと一致し、Commit が Succeeded ならディスクはモデル、それ以外は開始前のままで、作ったディレクトリとジャーナルは残らない</para>
    /// </remarks>
    [Fact]
    public async Task ディレクトリの操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            DirectoryScenario scenario = DirectoryScenario.Generate(baseSeed + i, maxOperations: 10, maxBytes);
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
    /// 同じシードは同じディレクトリ列を作る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB</para>
    /// <para>手順: 同じシードで列を 2 つ作る</para>
    /// <para>期待: 開始時の木、操作、終わり方が一致する</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードは同じディレクトリ列を作ること()
    {
        DirectoryScenario left = DirectoryScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        DirectoryScenario right = DirectoryScenario.Generate(7, 10, StressContent.DefaultMaxBytes);
        Assert.Equal(left.Describe(), right.Describe());
        Assert.Equal(left.Operations.Count, right.Operations.Count);
        for (int i = 0; i < left.Operations.Count; i++)
        {
            byte[]? leftContent = left.Operations[i].Content;
            byte[]? rightContent = right.Operations[i].Content;
            if (leftContent is null)
            {
                Assert.Null(rightContent);
            }
            else
            {
                Assert.True(leftContent.AsSpan().SequenceEqual(rightContent));
            }
        }
    }
}
