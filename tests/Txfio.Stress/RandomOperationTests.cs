namespace Txfio.Tests.Stress;

public sealed class RandomOperationTests
{
    /// <summary>
    /// Random sequences of Add / Update / Delete / Move / Read do not disagree with the model, even with content of different sizes.
    /// </summary>
    /// <remarks>
    /// <para>Given: a few paths at the root and under <c>sub</c> have files decided by the seed. Content lengths are chosen by the seed from empty, a few bytes, tens of KB, and several MB, and the count, seed, and length limit can be changed with environment variables.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed.</para>
    /// <para>Then: each step is applied as the model says or rejected with InvalidOperationException; ReadAsync along the way matches the model's bytes; if Commit is Succeeded the disk matches the model, otherwise it is as before the start; and no journal or .txnew remains.</para>
    /// </remarks>
    [Fact]
    public async Task RandomSequence_MatchesModel()
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
                    $"{StressSettings.SeedVariable}={scenario.Seed} broke a promise (shrunk sequence){Environment.NewLine}"
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same bytes, and with the default limit the lengths fall into four bands.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB.</para>
    /// <para>When: two sequences are made with the same seed, and many other seeds are made to check lengths.</para>
    /// <para>Then: the contents of the two sequences match, each length is empty, 32 bytes or less, 16 KiB to 64 KiB, or 1 MiB to 4 MiB, and all four bands appear.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameLengthsInDefaultBands()
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
                    Assert.Fail(length + " bytes is outside the band");
                }
            }
        }

        Assert.True(empty && fewBytes && tensOfKilobytes && fewMegabytes);
    }

    /// <summary>
    /// With a limit under 1 MiB the several-MB band does not appear, and with a limit above 4 MiB lengths reach that value.
    /// </summary>
    /// <remarks>
    /// <para>Given: a low limit under 1 MiB, and a high limit above 4 MiB.</para>
    /// <para>When: a sequence is made with each limit.</para>
    /// <para>Then: the low limit has nothing of 1 MiB or more, the high limit has a length above 4 MiB, and neither exceeds its limit.</para>
    /// </remarks>
    [Fact]
    public void Generate_LimitDecidesSeveralMegabyteBand()
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
