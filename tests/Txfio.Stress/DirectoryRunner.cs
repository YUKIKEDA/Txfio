using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// Runs a directory sequence on both a real transaction and an in-memory tree, looking for disagreements.
/// </summary>
internal static class DirectoryRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// Runs a sequence once, and returns a description if a promise is broken.
    /// </summary>
    /// <param name="scenario">The sequence to run.</param>
    /// <returns><see langword="null"/> if the promises hold, otherwise a description of the break.</returns>
    public static async Task<string?> RunAsync(DirectoryScenario scenario)
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteTreeAsync(work.Path, scenario.Initial);
        DirectoryTree model = scenario.Initial.Clone();
        StringBuilder trace = new StringBuilder();
        string? failure;
        try
        {
            failure = await RunOperationsAsync(work.Path, scenario, model, trace);
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
    public static async Task<(DirectoryScenario Scenario, string Failure)> ShrinkAsync(DirectoryScenario scenario, string failure)
    {
        int runs = 0;
        bool shrunk = true;
        while (shrunk && runs < MaxShrinkRuns)
        {
            shrunk = false;
            for (int i = 0; i < scenario.Operations.Count && runs < MaxShrinkRuns; i++)
            {
                List<DirectoryOperation> operations = scenario.Operations.ToList();
                operations.RemoveAt(i);
                DirectoryScenario candidate = scenario with { Operations = operations };
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

    private static async Task WriteTreeAsync(string workFolder, DirectoryTree tree)
    {
        foreach (string directory in tree.Directories.OrderBy(path => Depth(path)).ThenBy(path => path, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(FullPath(workFolder, directory));
        }

        foreach (string file in tree.Files)
        {
            await File.WriteAllBytesAsync(FullPath(workFolder, file), tree.File(file));
        }
    }

    private static async Task<string?> RunOperationsAsync(
        string workFolder,
        DirectoryScenario scenario,
        DirectoryTree model,
        StringBuilder trace)
    {
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
        try
        {
            List<DirectoryOperation> applied = new List<DirectoryOperation>();
            for (int i = 0; i < scenario.Operations.Count; i++)
            {
                DirectoryOperation operation = scenario.Operations[i];
                if (!operation.CanApply(model, applied))
                {
                    trace.Append("  ").Append(i).Append(": skip ").Append(operation).AppendLine();
                    continue;
                }

                string pendingBefore = DescribePending(tx);
                try
                {
                    if (operation.Kind == DirectoryOperationKind.Read)
                    {
                        string? mismatchRead = await CompareReadAsync(tx, model, operation.Path);
                        trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                        if (mismatchRead is not null)
                        {
                            return $"Step {i}: ReadAsync differs from the model: {mismatchRead}";
                        }

                        continue;
                    }

                    await ApplyAsync(tx, operation);
                    trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                    operation.ApplyTo(model);
                    applied.Add(operation);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ExternalConflictException)
                {
                    trace.Append("  ").Append(i).Append(": rejected ").Append(operation)
                        .Append(" (").Append(exception.Message).Append(')').AppendLine();
                    string pendingAfter = DescribePending(tx);
                    if (pendingAfter != pendingBefore)
                    {
                        return $"Step {i} was rejected but the schedule changed: before [{pendingBefore}], after [{pendingAfter}]";
                    }
                }

                string? mismatch = await CompareViewAsync(workFolder, tx, model);
                if (mismatch is not null)
                {
                    return $"Step {i}: the view afterwards differs from the model: {mismatch}";
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
                        expected = model;
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
            return await CompareDiskAsync(workFolder, expected);
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static async Task ApplyAsync(ITransaction tx, DirectoryOperation operation)
    {
        switch (operation.Kind)
        {
            case DirectoryOperationKind.CreateDirectory:
                await tx.CreateDirectoryAsync(operation.Path);
                break;
            case DirectoryOperationKind.Add:
                await using (MemoryStream added = new MemoryStream(operation.Content!))
                {
                    await tx.AddAsync(operation.Path, added);
                }

                break;
            case DirectoryOperationKind.Update:
                await using (MemoryStream updated = new MemoryStream(operation.Content!))
                {
                    await tx.UpdateAsync(operation.Path, updated);
                }

                break;
            case DirectoryOperationKind.Delete:
                await tx.DeleteAsync(operation.Path);
                break;
            case DirectoryOperationKind.DeleteTree:
                await tx.DeleteTreeAsync(operation.Path);
                break;
            case DirectoryOperationKind.Move:
                await tx.MoveAsync(operation.Path, operation.NewPath!, operation.Overwrite);
                break;
        }
    }

    private static async Task<string?> CompareViewAsync(string workFolder, ITransaction tx, DirectoryTree model)
    {
        foreach (string path in model.Files.Order(StringComparer.Ordinal))
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

        foreach (string directory in model.Directories.Order(StringComparer.Ordinal))
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

    private static async Task<string?> CompareDiskAsync(string workFolder, DirectoryTree expected)
    {
        DirectoryTree actual = new DirectoryTree();
        foreach (string directory in Directory.EnumerateDirectories(workFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(workFolder, directory);
            if (!relative.StartsWith(".txfio", StringComparison.Ordinal))
            {
                actual.AddDirectory(relative);
            }
        }

        foreach (string file in Directory.EnumerateFiles(workFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(workFolder, file);
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                actual.PutFile(relative, await File.ReadAllBytesAsync(file));
            }
        }

        foreach (string directory in expected.Directories.Order(StringComparer.Ordinal))
        {
            if (!actual.IsDirectory(directory))
            {
                return "Directory missing at the end: " + directory;
            }
        }

        foreach (string directory in actual.Directories.Order(StringComparer.Ordinal))
        {
            if (!expected.IsDirectory(directory))
            {
                return "Extra directory at the end: " + directory;
            }
        }

        foreach (string file in expected.Files.Order(StringComparer.Ordinal))
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

        foreach (string file in actual.Files.Order(StringComparer.Ordinal))
        {
            if (!expected.IsFile(file))
            {
                return "Extra file at the end: " + file + " (" + StressContent.Describe(actual.File(file)) + ")";
            }
        }

        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        if (Directory.Exists(metadata) && Directory.EnumerateFiles(metadata, "tx-*.journal").Any())
        {
            return "A journal remained at the end";
        }

        return null;
    }

    private static string DescribePending(ITransaction tx)
    {
        return string.Join(", ", tx.GetPendingChanges().Select(change => $"{change.Kind}:{change.Path}>{change.NewPath}"));
    }

    private static string Relative(string workFolder, string path)
    {
        return System.IO.Path.GetRelativePath(workFolder, path).Replace('\\', '/');
    }

    private static string FullPath(string workFolder, string relative)
    {
        return System.IO.Path.Combine(workFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
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
