namespace Txfio.Tests.Stress;

public sealed class RandomOperationTests
{
    /// <summary>
    /// ランダムな Add / Update / Delete / Move / Read の列が、大きさの違う中身でもモデルと食い違わない
    /// </summary>
    /// <remarks>
    /// <para>前提: ルートと <c>sub</c> の下の少数のパスに、シードで決めたファイルがある。中身の長さは空、数バイト、数十 KB、数 MB からシードが選び、本数とシードと長さの上限は環境変数で変えられる</para>
    /// <para>手順: 1 つのトランザクションでシードごとの操作列を打ち、Commit か Dispose する</para>
    /// <para>期待: 各手はモデルどおりに反映されるか InvalidOperationException で拒否され、途中の ReadAsync はモデルのバイト列と一致し、Commit が Succeeded ならディスクはモデル、それ以外は開始前のままで、ジャーナルと .txnew は残らない</para>
    /// </remarks>
    [Fact]
    public async Task ランダムな操作列_モデルと同じ結果になること()
    {
        int baseSeed = StressSettings.Seed(1);
        int count = StressSettings.Iterations(40);
        int maxBytes = StressSettings.MaxBytes(StressContent.DefaultMaxBytes);
        for (int i = 0; i < count; i++)
        {
            RandomOperationScenario scenario = RandomOperationScenario.Generate(baseSeed + i, maxOperations: 10, maxBytes);
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

    /// <summary>
    /// 同じシードは同じバイト列を作り、既定の上限では長さが四つの帯に入る
    /// </summary>
    /// <remarks>
    /// <para>前提: 長さの上限は 4 MiB</para>
    /// <para>手順: 同じシードで列を 2 つ作り、別のシードも多数作って長さを見る</para>
    /// <para>期待: 2 つの列の中身は一致し、長さは空、32 バイト以下、16 KiB から 64 KiB、1 MiB から 4 MiB のいずれかで、四つの帯がどれも現れる</para>
    /// </remarks>
    [Fact]
    public void Generate_同じシードは同じ長さになり既定の帯に入ること()
    {
        const int maxOperations = 10;
        RandomOperationScenario left = RandomOperationScenario.Generate(7, maxOperations, StressContent.DefaultMaxBytes);
        RandomOperationScenario right = RandomOperationScenario.Generate(7, maxOperations, StressContent.DefaultMaxBytes);
        AssertSameContent(left, right);

        bool empty = false;
        bool fewBytes = false;
        bool tensOfKilobytes = false;
        bool fewMegabytes = false;
        for (int seed = 0; seed < 80; seed++)
        {
            RandomOperationScenario scenario = RandomOperationScenario.Generate(seed, maxOperations, StressContent.DefaultMaxBytes);
            foreach (byte[] content in Contents(scenario))
            {
                int length = content.Length;
                if (length == 0)
                {
                    empty = true;
                }
                else if (length <= 32)
                {
                    fewBytes = true;
                }
                else if (length >= 16 * 1024 && length <= 64 * 1024)
                {
                    tensOfKilobytes = true;
                }
                else if (length >= 1024 * 1024 && length <= StressContent.DefaultMaxBytes)
                {
                    fewMegabytes = true;
                }
                else
                {
                    Assert.Fail(length + " バイトは帯の外");
                }
            }
        }

        Assert.True(empty && fewBytes && tensOfKilobytes && fewMegabytes);
    }

    /// <summary>
    /// 上限が 1 MiB 未満なら数 MB の帯は出ず、上限を 4 MiB より上げるとその値まで届く
    /// </summary>
    /// <remarks>
    /// <para>前提: 低い上限は 1 MiB 未満、高い上限は 4 MiB より大きい</para>
    /// <para>手順: それぞれの上限で列を作る</para>
    /// <para>期待: 低い上限では 1 MiB 以上が無く、高い上限では 4 MiB を超える長さがあり、どちらも上限は超えない</para>
    /// </remarks>
    [Fact]
    public void Generate_上限で数MBの帯の有無が変わること()
    {
        const int maxOperations = 10;
        int belowMegabyte = (1024 * 1024) - 1;
        for (int seed = 0; seed < 40; seed++)
        {
            foreach (byte[] content in Contents(RandomOperationScenario.Generate(seed, maxOperations, belowMegabyte)))
            {
                Assert.True(content.Length < 1024 * 1024);
            }
        }

        int raised = StressContent.DefaultMaxBytes + (256 * 1024);
        bool aboveDefault = false;
        for (int seed = 0; seed < 40; seed++)
        {
            foreach (byte[] content in Contents(RandomOperationScenario.Generate(seed, maxOperations, raised)))
            {
                Assert.True(content.Length <= raised);
                if (content.Length > StressContent.DefaultMaxBytes)
                {
                    aboveDefault = true;
                }
            }
        }

        Assert.True(aboveDefault);
    }

    private static void AssertSameContent(RandomOperationScenario left, RandomOperationScenario right)
    {
        Assert.Equal(left.Operations.Count, right.Operations.Count);
        Assert.Equal(left.InitialFiles.Count, right.InitialFiles.Count);
        foreach (KeyValuePair<string, byte[]> file in left.InitialFiles)
        {
            Assert.True(file.Value.AsSpan().SequenceEqual(right.InitialFiles[file.Key]));
        }

        for (int i = 0; i < left.Operations.Count; i++)
        {
            Assert.Equal(left.Operations[i].Kind, right.Operations[i].Kind);
            Assert.Equal(left.Operations[i].Path, right.Operations[i].Path);
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

    private static IEnumerable<byte[]> Contents(RandomOperationScenario scenario)
    {
        foreach (byte[] content in scenario.InitialFiles.Values)
        {
            yield return content;
        }

        foreach (RandomOperation operation in scenario.Operations)
        {
            if (operation.Content is not null)
            {
                yield return operation.Content;
            }
        }
    }
}
