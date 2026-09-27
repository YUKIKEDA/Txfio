namespace Txfio.Tests.Stress;

public sealed class DirectoryOperationTests
{
    /// <summary>
    /// A sequence with directory create, delete, tree delete, and Move without overwrite does not disagree with the tree model.
    /// </summary>
    /// <remarks>
    /// <para>Given: d and e exist from the start, and other directories and files are placed by the seed. File lengths are chosen by the seed from empty, a few bytes, tens of KB, and several MB.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed.</para>
    /// <para>Then: each step is applied as the model says, or rejected with InvalidOperationException or ExternalConflictException; ReadAsync and GetEntriesAsync along the way match the model; if Commit is Succeeded the disk matches the model, otherwise it is as before the start; and no created directory or journal remains.</para>
    /// </remarks>
    [Fact]
    public async Task DirectorySequence_MatchesModel()
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
                    $"{StressSettings.SeedVariable}={scenario.Seed} broke a promise (shrunk sequence){Environment.NewLine}"
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same directory sequence.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB.</para>
    /// <para>When: two sequences are made with the same seed.</para>
    /// <para>Then: the starting tree, the operations, and how it ends match.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameDirectorySequence()
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
