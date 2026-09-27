namespace Txfio.Tests.Stress;

public sealed class TransferOperationTests
{
    /// <summary>
    /// A sequence with Copy, Import, and Export of files and directories does not disagree with the tree model.
    /// </summary>
    /// <remarks>
    /// <para>Given: d, e, and a.txt exist from the start, and outside there are files and directories. File lengths are chosen by the seed from empty, a few bytes, tens of KB, and several MB.</para>
    /// <para>When: the sequence for each seed runs in one transaction, which then commits or is disposed, and then RecoverAsync runs.</para>
    /// <para>Then: steps that pass are applied as the model says, rejected steps do not change the model, ReadAsync and GetEntriesAsync along the way match the model, and if Commit is Succeeded the work folder matches the model, otherwise it is as before the start. What was exported keeps its content from copy time, and remains after discard and after RecoverAsync.</para>
    /// </remarks>
    [Fact]
    public async Task CopyImportExportSequence_MatchesModel()
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
                    StressSettings.SeedVariable + "=" + scenario.Seed + " broke a promise (shrunk sequence)" + Environment.NewLine
                    + shrunk.Describe() + Environment.NewLine + shrunkFailure);
            }
        }
    }

    /// <summary>
    /// The same seed makes the same sequence, including Copy, Import, and Export of files and directories.
    /// </summary>
    /// <remarks>
    /// <para>Given: a length limit of 4 MiB. Outside there is a file in-file and a directory in-dir.</para>
    /// <para>When: two sequences are made with the same seed, and many other seeds are checked.</para>
    /// <para>Then: the two sequences match, and every sequence has Copy, Import, and Export of files and directories.</para>
    /// </remarks>
    [Fact]
    public void Generate_SameSeedMakesSameSequenceWithCopyImportExport()
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
