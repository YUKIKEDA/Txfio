using System.IO.Compression;
using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// Runs a ZIP sequence on both a real transaction and an in-memory tree, looking for disagreements.
/// </summary>
internal static class ArchiveRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// Runs a sequence once, and returns a description if a promise is broken.
    /// </summary>
    /// <param name="scenario">The sequence to run.</param>
    /// <returns><see langword="null"/> if the promises hold, otherwise a description of the break.</returns>
    public static async Task<string?> RunAsync(ArchiveScenario scenario)
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await WriteTreeAsync(work.Path, scenario.Initial);
        await WriteImportZipAsync(outside.Path, scenario.ImportContent);
        ArchiveWorld world = new ArchiveWorld(scenario.Initial, scenario.ImportContent);
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
    public static async Task<(ArchiveScenario Scenario, string Failure)> ShrinkAsync(ArchiveScenario scenario, string failure)
    {
        int runs = 0;
        bool shrunk = true;
        while (shrunk && runs < MaxShrinkRuns)
        {
            shrunk = false;
            for (int i = 0; i < scenario.Operations.Count && runs < MaxShrinkRuns; i++)
            {
                List<ArchiveOperation> operations = scenario.Operations.ToList();
                operations.RemoveAt(i);
                ArchiveScenario candidate = scenario with { Operations = operations };
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
        ArchiveScenario scenario,
        ArchiveWorld world,
        StringBuilder trace)
    {
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
        try
        {
            for (int i = 0; i < scenario.Operations.Count; i++)
            {
                ArchiveOperation operation = scenario.Operations[i];
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
                        string? archive = await CaptureArchiveAsync(tx, outsideRoot, world, operation);
                        if (archive is not null)
                        {
                            return "Step " + i + ": the ZIP differs from the model: " + archive;
                        }
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

            return await CompareExportsAsync(outsideRoot, world);
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static async Task<string?> CaptureArchiveAsync(
        ITransaction tx,
        string outsideRoot,
        ArchiveWorld world,
        ArchiveOperation operation)
    {
        if (operation.Kind == ArchiveKind.Create)
        {
            byte[] bytes = await ReadAsync(tx, operation.Destination);
            string? mismatch = await CompareZipBytesAsync(bytes, world.ArchiveEntries(operation.Destination));
            world.Commit.PutFile(operation.Destination, bytes);
            return mismatch;
        }

        if (operation.Kind == ArchiveKind.Export)
        {
            byte[] bytes = await File.ReadAllBytesAsync(FullPath(outsideRoot, operation.Destination));
            return await CompareZipBytesAsync(bytes, world.ExportEntries(operation.Destination));
        }

        return null;
    }

    private static async Task ApplyAsync(ITransaction tx, string outsideRoot, ArchiveOperation operation)
    {
        switch (operation.Kind)
        {
            case ArchiveKind.Create:
                await tx.CreateArchiveAsync(operation.Source, operation.Destination, includeBaseDirectory: operation.IncludeBase);
                break;
            case ArchiveKind.Extract:
                await tx.ExtractArchiveAsync(operation.Source, operation.Destination);
                break;
            case ArchiveKind.Import:
                await tx.ImportArchiveAsync(FullPath(outsideRoot, operation.Source), operation.Destination);
                break;
            default:
                await tx.ExportArchiveAsync(operation.Source, FullPath(outsideRoot, operation.Destination), includeBaseDirectory: operation.IncludeBase);
                break;
        }
    }

    private static async Task WriteImportZipAsync(string outsideRoot, byte[] content)
    {
        Directory.CreateDirectory(FullPath(outsideRoot, "out"));
        await using FileStream stream = File.Create(FullPath(outsideRoot, "in.zip"));
        using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry file = zip.CreateEntry("a.txt");
            await using (Stream entry = file.Open())
            {
                await entry.WriteAsync(content);
            }

            zip.CreateEntry("empty/");
        }
    }

    private static async Task<string?> CompareExportsAsync(string outsideRoot, ArchiveWorld world)
    {
        foreach (string path in world.ExportPaths)
        {
            string full = FullPath(outsideRoot, path);
            if (!File.Exists(full))
            {
                return "The exported ZIP is gone: " + path;
            }

            byte[] bytes = await File.ReadAllBytesAsync(full);
            string? mismatch = await CompareZipBytesAsync(bytes, world.ExportEntries(path));
            if (mismatch is not null)
            {
                return path + ": " + mismatch;
            }
        }

        return null;
    }

    private static async Task<string?> CompareZipBytesAsync(byte[] bytes, Dictionary<string, byte[]?> expected)
    {
        Dictionary<string, byte[]?> actual = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        using (MemoryStream stream = new MemoryStream(bytes, writable: false))
        {
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    actual[entry.FullName] = null;
                    continue;
                }

                using Stream entryStream = entry.Open();
                using MemoryStream copy = new MemoryStream();
                await entryStream.CopyToAsync(copy);
                actual[entry.FullName] = copy.ToArray();
            }
        }

        foreach (KeyValuePair<string, byte[]?> pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out byte[]? found))
            {
                return "Entry missing: " + pair.Key;
            }

            if (pair.Value is null)
            {
                if (found is not null)
                {
                    return pair.Key + " was not a directory";
                }

                continue;
            }

            if (found is null || !pair.Value.AsSpan().SequenceEqual(found))
            {
                return pair.Key + " has different content";
            }
        }

        foreach (string key in actual.Keys)
        {
            if (!expected.ContainsKey(key))
            {
                return "Extra entry: " + key;
            }
        }

        return null;
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
            byte[] expected = model.File(path);
            byte[] actual = await ReadAsync(tx, path);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                return path + " expected " + StressContent.Describe(expected) + ", actual " + StressContent.Describe(actual);
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
                return "File missing at the end: " + file;
            }

            if (!expected.File(file).AsSpan().SequenceEqual(actual.File(file)))
            {
                return "File differs at the end: " + file;
            }
        }

        foreach (string file in actual.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expected.IsFile(file))
            {
                return "Extra file at the end: " + file;
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
