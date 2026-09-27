using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// A copy, import, and export sequence for one transaction, and the trees at the start.
/// </summary>
/// <param name="Seed">The seed that made this sequence.</param>
/// <param name="Initial">The tree placed in the work folder before the start.</param>
/// <param name="Outside">The tree placed outside the work folder before the start.</param>
/// <param name="Operations">The operations to run in order.</param>
/// <param name="Commit">true to Commit at the end, false to only Dispose.</param>
internal sealed record TransferScenario(
    int Seed,
    DirectoryTree Initial,
    DirectoryTree Outside,
    IReadOnlyList<TransferOperation> Operations,
    bool Commit)
{
    private static readonly string[] _destinations = new[] { "b.txt", "c.txt", "d/b.txt", "e/a.txt", "g", "h", "p", "d/g", "e/g", "q.txt" };

    /// <summary>
    /// Makes a sequence from a seed. Copy, Import, and Export of files and directories go first as steps that pass.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <param name="maxOperations">The maximum number of operations.</param>
    /// <param name="maxBytes">The maximum length of one file.</param>
    /// <returns>The sequence that was made.</returns>
    public static TransferScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        DirectoryTree initial = DirectoryScenario.CreateInitial(random, maxBytes);
        if (!initial.IsFile("a.txt"))
        {
            initial.PutFile("a.txt", StressContent.Create(random, maxBytes));
        }

        DirectoryTree outside = CreateOutside(random, maxBytes);
        TransferWorld world = new TransferWorld(initial, outside);
        List<TransferOperation> operations = new List<TransferOperation>();
        TryAppend(world, operations, new TransferOperation(TransferKind.Copy, "d", "g"));
        TryAppend(world, operations, new TransferOperation(TransferKind.Import, "in-dir", "h"));
        TryAppend(world, operations, new TransferOperation(TransferKind.Export, "d", world.NextExportPath()));
        TryAppend(world, operations, new TransferOperation(TransferKind.Import, "in-file", "b.txt"));
        TryAppend(world, operations, new TransferOperation(TransferKind.Export, "b.txt", world.NextExportPath()));
        TryAppend(world, operations, new TransferOperation(TransferKind.Copy, "a.txt", "c.txt"));

        int count = random.Next(operations.Count, Math.Max(operations.Count, maxOperations) + 1);
        for (int i = operations.Count; i < count; i++)
        {
            TransferOperation operation = Next(random, world);
            if (operation.CanApply(world))
            {
                operation.ApplyTo(world);
            }

            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new TransferScenario(seed, initial, outside, operations, commit);
    }

    /// <summary>
    /// A readable form of the sequence for failure reports.
    /// </summary>
    /// <returns>A string with the starting tree, outside, the operations, and how it ends.</returns>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();
        text.Append("seed=").Append(Seed).AppendLine();
        text.Append("initial dirs=[").Append(string.Join(", ", Initial.Directories.OrderBy(path => path, StringComparer.Ordinal))).AppendLine("]");
        text.Append("initial files=[").Append(string.Join(", ", Initial.Files.OrderBy(path => path, StringComparer.Ordinal).Select(path => path + " " + StressContent.Describe(Initial.File(path))))).AppendLine("]");
        text.Append("outside dirs=[").Append(string.Join(", ", Outside.Directories.OrderBy(path => path, StringComparer.Ordinal))).AppendLine("]");
        text.Append("outside files=[").Append(string.Join(", ", Outside.Files.OrderBy(path => path, StringComparer.Ordinal).Select(path => path + " " + StressContent.Describe(Outside.File(path))))).AppendLine("]");
        for (int i = 0; i < Operations.Count; i++)
        {
            text.Append("  ").Append(i).Append(": ").Append(Operations[i]).AppendLine();
        }

        text.Append(Commit ? "  Commit" : "  Dispose");
        return text.ToString();
    }

    private static DirectoryTree CreateOutside(Random random, int maxBytes)
    {
        DirectoryTree outside = new DirectoryTree();
        outside.PutFile("in-file", StressContent.Create(random, maxBytes));
        outside.AddDirectory("in-dir");
        outside.PutFile("in-dir/a.txt", StressContent.Create(random, maxBytes));
        outside.AddDirectory("in-dir/sub");
        outside.PutFile("in-dir/sub/b.txt", StressContent.Create(random, maxBytes));
        outside.AddDirectory("in-dir/empty");
        outside.AddDirectory("out");
        return outside;
    }

    private static void TryAppend(TransferWorld world, List<TransferOperation> operations, TransferOperation operation)
    {
        if (!operation.CanApply(world))
        {
            return;
        }

        operation.ApplyTo(world);
        operations.Add(operation);
    }

    private static TransferOperation Next(Random random, TransferWorld world)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            if (random.Next(4) == 0)
            {
                TransferOperation rejected = Reject(random, world);
                if (!rejected.CanApply(world))
                {
                    return rejected;
                }

                continue;
            }

            TransferOperation? legal = TryLegal(random, world);
            if (legal is not null)
            {
                return legal;
            }
        }

        return new TransferOperation(TransferKind.Copy, "d", "d");
    }

    private static TransferOperation? TryLegal(Random random, TransferWorld world)
    {
        TransferKind kind = (TransferKind)random.Next(3);
        if (kind == TransferKind.Copy)
        {
            List<string> sources = world.Disk.Directories.Concat(world.Disk.Files).ToList();
            if (sources.Count == 0)
            {
                return null;
            }

            TransferOperation operation = new TransferOperation(TransferKind.Copy, Pick(random, sources), Pick(random, _destinations));
            return operation.CanApply(world) ? operation : null;
        }

        if (kind == TransferKind.Import)
        {
            string source = random.Next(2) == 0 ? "in-file" : "in-dir";
            TransferOperation operation = new TransferOperation(TransferKind.Import, source, Pick(random, _destinations));
            return operation.CanApply(world) ? operation : null;
        }

        List<string> exportSources = world.Disk.Directories.Concat(world.Commit.Files).ToList();
        if (exportSources.Count == 0)
        {
            return null;
        }

        TransferOperation export = new TransferOperation(TransferKind.Export, Pick(random, exportSources), world.NextExportPath());
        return export.CanApply(world) ? export : null;
    }

    private static TransferOperation Reject(Random random, TransferWorld world)
    {
        TransferOperation[] options = new TransferOperation[]
        {
            new TransferOperation(TransferKind.Copy, "missing.txt", "q.txt"),
            new TransferOperation(TransferKind.Copy, "d", "d"),
            new TransferOperation(TransferKind.Copy, "d", "nope/a.txt"),
            new TransferOperation(TransferKind.Import, "in-dir", "d"),
            new TransferOperation(TransferKind.Import, "missing-outside", "q.txt"),
            new TransferOperation(TransferKind.Import, "in-file", "nope/a.txt"),
            new TransferOperation(TransferKind.Export, "missing.txt", "out/x"),
            new TransferOperation(TransferKind.Export, "d", "out"),
            new TransferOperation(TransferKind.Export, "d", "gone/a.txt"),
        };
        List<TransferOperation> rejected = new List<TransferOperation>();
        foreach (TransferOperation option in options)
        {
            if (!option.CanApply(world))
            {
                rejected.Add(option);
            }
        }

        if (rejected.Count == 0)
        {
            return new TransferOperation(TransferKind.Copy, "d", "d");
        }

        return Pick(random, rejected);
    }

    private static string Pick(Random random, IReadOnlyList<string> paths)
    {
        return paths[random.Next(paths.Count)];
    }

    private static TransferOperation Pick(Random random, IReadOnlyList<TransferOperation> operations)
    {
        return operations[random.Next(operations.Count)];
    }
}
