namespace Txfio.Tests.Stress;

public sealed class ArchiveOperationTests
{
    /// <summary>
    /// A sequence with ZIP create, extract, import, and export does not disagree with the tree model.
    /// </summary>
    /// <remarks>
    /// <para>Given: d has a.txt, sub/b.txt, and an empty empty, and a.txt is also directly under the work folder. Content lengths range from empty to several MB. Outside there is in.zip with a.txt and an empty directory.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed.</para>
    /// <para>Then: a created ZIP read back matches the files put in; after extract and import the work folder matches the model; rejected steps do not change the model; if Commit is Succeeded the disk matches the model, otherwise it is as before the start. A ZIP written outside remains after discard.</para>
    /// </remarks>
    [Fact]
    public async Task ZipSequence_MatchesModel()
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
                    StressSettings.SeedVariable + "=" + scenario.Seed + " broke a promise (shrunk sequence)" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same sequence, with create, extract, import, and export.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB. d and a.txt exist from the start.</para>
    /// <para>When: two sequences are made with the same seed, and many other seeds are checked.</para>
    /// <para>Then: the two sequences match, and every sequence has create, extract, import, and export.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameZipSequence()
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
