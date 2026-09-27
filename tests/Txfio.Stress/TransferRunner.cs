using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// Runs a copy, import, and export sequence on both a real transaction and an in-memory tree, looking for disagreements.
/// </summary>
internal static class TransferRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// Runs a sequence once, and returns a description if a promise is broken.
    /// </summary>
    /// <param name="scenario">The sequence to run.</param>
    /// <returns><see langword="null"/> if the promises hold, otherwise a description of the break.</returns>
    public static async Task<string?> RunAsync(TransferScenario scenario)
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await WriteTreeAsync(work.Path, scenario.Initial);
        await WriteTreeAsync(outside.Path, scenario.Outside);
        TransferWorld world = new TransferWorld(scenario.Initial, scenario.Outside);
        StringBuilder trace = new StringBuilder();
        string? failure;
        try
        {
            failure = await RunOperationsAsync(work.Path, outside.Path, scenario, world, trace);
        }
        catch (Exception exception)
        {
            failure = "Unexpected exception: " + exception;
        }

        return failure is null ? null : failure + Environment.NewLine + "Steps run:" + Environment.NewLine + trace;
    }

    /// <summary>
    /// Removes steps one at a time from a failed sequence, shrinking it to the smallest one that still fails.
    /// </summary>
    /// <param name="scenario">The failed sequence.</param>
    /// <param name="failure">The description of that failure.</param>
    /// <returns>The shrunk sequence and its failure description.</returns>
    public static async Task<(TransferScenario Scenario, string Failure)> ShrinkAsync(TransferScenario scenario, string failure)
    {
        int runs = 0;
        bool shrunk = true;
        while (shrunk && runs < MaxShrinkRuns)
        {
            shrunk = false;
            for (int i = 0; i < scenario.Operations.Count && runs < MaxShrinkRuns; i++)
            {
                List<TransferOperation> operations = scenario.Operations.ToList();
                operations.RemoveAt(i);
                TransferScenario candidate = scenario with { Operations = operations };
                runs++;
                string? candidateFailure = await RunAsync(candidate);
                if (candidateFailure is not null)
                {
                    scenario = candidate;
                    failure = candidateFailure;
                    shrunk = true;
                    break;
                }
            }
        }

        return (scenario, failure);
    }

    private static async Task<string?> RunOperationsAsync(
        string workFolder,
        string outsideRoot,
        TransferScenario scenario,
        TransferWorld world,
        StringBuilder trace)
    {
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
        try
        {
            for (int i = 0; i < scenario.Operations.Count; i++)
            {
                TransferOperation operation = scenario.Operations[i];
                string pendingBefore = DescribePending(tx);
                if (!operation.CanApply(world))
                {
                    try
                    {
                        await ApplyAsync(tx, outsideRoot, operation);
                        return "Step " + i + " should have been rejected but passed: " + operation;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ExternalConflictException)
                    {
                        trace.Append("  ").Append(i).Append(": rejected ").Append(operation)
                            .Append(" (").Append(exception.Message).Append(')').AppendLine();
                        string pendingAfter = DescribePending(tx);
                        if (pendingAfter != pendingBefore)
                        {
                            return "Step " + i + " was rejected but the schedule changed: before [" + pendingBefore + "], after [" + pendingAfter + "]";
                        }
                    }
                }
                else
                {
                    try
                    {
                        await ApplyAsync(tx, outsideRoot, operation);
                        trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                        operation.ApplyTo(world);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ExternalConflictException)
                    {
                        return "Step " + i + " should have passed but was rejected: " + operation + " (" + exception.Message + ")";
                    }
                }

                string? mismatch = await CompareViewAsync(workFolder, tx, world.Commit);
                if (mismatch is not null)
                {
                    return "Step " + i + ": the view afterwards differs from the model: " + mismatch;
                }
            }

            DirectoryTree expected = scenario.Initial;
            if (scenario.Commit)
            {
                try
                {
                    CommitReport report = await tx.CommitAsync();
                    trace.Append("  Commit -> ").Append(report.Result).AppendLine();
                    if (report.Result == CommitResult.PartialConflict)
                    {
                        return "PartialConflict without any external change";
                    }

                    if (report.Result == CommitResult.Succeeded)
                    {
                        expected = world.Commit;
                    }
                }
                catch (InvalidOperationException exception)
                {
                    trace.Append("  Commit rejected (").Append(exception.Message).Append(')').AppendLine();
                }
            }
            else
            {
                trace.Append("  Dispose").AppendLine();
            }

            await tx.DisposeAsync();
            string? disk = await CompareDiskAsync(workFolder, expected);
            if (disk is not null)
            {
                return disk;
            }

            string? exported = await CompareDiskAsync(outsideRoot, world.ExpectedOutside());
            if (exported is not null)
            {
                return "The export differs from the outside at copy time: " + exported;
            }

            await global::Txfio.Txfio.RecoverAsync(workFolder);
            string? afterRecover = await CompareDiskAsync(outsideRoot, world.ExpectedOutside());
            if (afterRecover is not null)
            {
                return "The export disappeared or changed after RecoverAsync: " + afterRecover;
            }

            return await CompareDiskAsync(workFolder, expected);
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static async Task ApplyAsync(ITransaction tx, string outsideRoot, TransferOperation operation)
    {
        switch (operation.Kind)
        {
            case TransferKind.Copy:
                await tx.CopyAsync(operation.Source, operation.Destination);
                break;
            case TransferKind.Import:
                await tx.ImportAsync(FullPath(outsideRoot, operation.Source), operation.Destination);
                break;
            default:
                await tx.ExportAsync(operation.Source, FullPath(outsideRoot, operation.Destination));
                break;
        }
    }

    private static async Task WriteTreeAsync(string root, DirectoryTree tree)
    {
        foreach (string directory in tree.Directories.OrderBy(path => Depth(path)).ThenBy(path => path, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(FullPath(root, directory));
        }

        foreach (string file in tree.Files)
        {
            await File.WriteAllBytesAsync(FullPath(root, file), tree.File(file));
        }
    }

    private static async Task<string?> CompareViewAsync(string workFolder, ITransaction tx, DirectoryTree model)
    {
        foreach (string path in model.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            string? mismatch = await CompareReadAsync(tx, model, path);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }

        string? root = await CompareEntriesAsync(workFolder, tx, model, string.Empty);
        if (root is not null)
        {
            return root;
        }

        foreach (string directory in model.Directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            string? mismatch = await CompareEntriesAsync(workFolder, tx, model, directory);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }

        return null;
    }

    private static async Task<string?> CompareReadAsync(ITransaction tx, DirectoryTree model, string path)
    {
        byte[] expected = model.File(path);
        byte[] actual = await ReadAsync(tx, path);
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return null;
        }

        if (expected.Length == actual.Length)
        {
            return path + " has length " + StressContent.Describe(expected) + " and different content";
        }

        return path + " expected " + StressContent.Describe(expected) + ", actual " + StressContent.Describe(actual);
    }

    private static async Task<string?> CompareEntriesAsync(string workFolder, ITransaction tx, DirectoryTree model, string directory)
    {
        IReadOnlyList<DirectoryEntry> entries = directory.Length == 0
            ? await tx.GetEntriesAsync(".")
            : await tx.GetEntriesAsync(directory);
        List<(string Path, bool IsDirectory)> actual = new List<(string Path, bool IsDirectory)>();
        foreach (DirectoryEntry entry in entries)
        {
            string relative = System.IO.Path.GetRelativePath(workFolder, entry.Path).Replace('\\', '/');
            actual.Add((relative, entry.IsDirectory));
        }

        actual.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal));
        IReadOnlyList<(string Path, bool IsDirectory)> expected = model.Children(directory);
        if (actual.Count != expected.Count)
        {
            return directory + " has a different number of direct children: expected " + expected.Count + ", actual " + actual.Count;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual[i].Path != expected[i].Path || actual[i].IsDirectory != expected[i].IsDirectory)
            {
                return directory + " has different direct children: expected " + expected[i].Path + ", actual " + actual[i].Path;
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        using MemoryStream copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static async Task<string?> CompareDiskAsync(string root, DirectoryTree expected)
    {
        DirectoryTree actual = new DirectoryTree();
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(root, directory);
            if (!relative.StartsWith(".txfio", StringComparison.Ordinal))
            {
                actual.AddDirectory(relative);
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(root, file);
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                actual.PutFile(relative, await File.ReadAllBytesAsync(file));
            }
        }

        foreach (string directory in expected.Directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!actual.IsDirectory(directory))
            {
                return "Directory missing at the end: " + directory;
            }
        }

        foreach (string directory in actual.Directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expected.IsDirectory(directory))
            {
                return "Extra directory at the end: " + directory;
            }
        }

        foreach (string file in expected.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!actual.IsFile(file))
            {
                return "File missing at the end: " + file + " (" + StressContent.Describe(expected.File(file)) + ")";
            }

            byte[] found = actual.File(file);
            if (!expected.File(file).AsSpan().SequenceEqual(found))
            {
                return "File differs at the end: " + file + " expected " + StressContent.Describe(expected.File(file)) + ", actual " + StressContent.Describe(found);
            }
        }

        foreach (string file in actual.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expected.IsFile(file))
            {
                return "Extra file at the end: " + file + " (" + StressContent.Describe(actual.File(file)) + ")";
            }
        }

        string metadata = System.IO.Path.Combine(root, ".txfio");
        if (Directory.Exists(metadata) && Directory.EnumerateFiles(metadata, "tx-*.journal").Any())
        {
            return "A journal remained at the end";
        }

        return null;
    }

    private static string DescribePending(ITransaction tx)
    {
        return string.Join(", ", tx.GetPendingChanges().Select(change => change.Kind + ":" + change.Path + ">" + change.NewPath));
    }

    private static string Relative(string root, string path)
    {
        return System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static string FullPath(string root, string relative)
    {
        return System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private static int Depth(string path)
    {
        int depth = 1;
        foreach (char character in path)
        {
            if (character == '/')
            {
                depth++;
            }
        }

        return depth;
    }
}
