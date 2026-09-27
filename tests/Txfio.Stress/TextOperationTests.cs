namespace Txfio.Tests.Stress;

public sealed class TextOperationTests
{
    /// <summary>
    /// A sequence with text and JSON reads and writes does not disagree with the text model.
    /// </summary>
    /// <remarks>
    /// <para>Given: d, e, and a.txt exist from the start. Text lengths are chosen by the seed from empty, a few characters, and tens of KB. Several MB is used only when the length limit is raised above the default.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed.</para>
    /// <para>Then: reading back after writes and appends matches the model, JSON matches the value written, rejected steps do not change the model, and if Commit is Succeeded the bytes on disk match the model, otherwise they are as before the start.</para>
    /// </remarks>
    [Fact]
    public async Task TextAndJsonSequence_MatchesModel()
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
                    StressSettings.SeedVariable + "=" + scenario.Seed + " broke a promise (shrunk sequence)" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same sequence, including every text and JSON API.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB, but the text in this sequence is at most tens of KB.</para>
    /// <para>When: two sequences are made with the same seed, and many other seeds are checked.</para>
    /// <para>Then: the two sequences match, and every sequence has Write, Read, Append, lines, and JSON writes and reads.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameSequenceWithTextAndJson()
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
