namespace Txfio.Tests.Stress;

public sealed class OverwriteOperationTests
{
    /// <summary>
    /// A sequence with Moves that replace their destination does not disagree with the tree model.
    /// </summary>
    /// <remarks>
    /// <para>Given: d and e exist from the start, and other directories and files are placed by the seed. The sequence starts with a replacing Move when one can be made.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed.</para>
    /// <para>Then: after the replacement the destination is the source's tree; later steps touching the source or destination are rejected without changing the model; if Commit is Succeeded the disk matches the model and no .txold remains; otherwise it is as before the start.</para>
    /// </remarks>
    [Fact]
    public async Task OverwriteMoveSequence_MatchesModel()
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
                    $"{StressSettings.SeedVariable}={scenario.Seed} broke a promise (shrunk sequence){Environment.NewLine}"
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same sequence, including a replacement.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB. d and e exist from the start.</para>
    /// <para>When: two sequences are made with the same seed, and many other seeds are checked.</para>
    /// <para>Then: the two sequences match, and every sequence has one replacing Move.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameSequenceWithReplacement()
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
