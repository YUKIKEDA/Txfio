using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// A directory sequence for one transaction, and the tree at the start.
/// </summary>
/// <param name="Seed">The seed that made this sequence.</param>
/// <param name="Initial">The tree placed on disk before the start.</param>
/// <param name="Operations">The operations to run in order.</param>
/// <param name="Commit">true to Commit at the end, false to only Dispose.</param>
internal sealed record DirectoryScenario(
    int Seed,
    DirectoryTree Initial,
    IReadOnlyList<DirectoryOperation> Operations,
    bool Commit)
{
    /// <summary>
    /// The directories that always exist.
    /// </summary>
    public static readonly IReadOnlyList<string> RootDirectories = new[] { "d", "e" };

    internal static readonly IReadOnlyList<string> DirectoryPaths = new[] { "d", "e", "d/c", "e/c", "f" };

    internal static readonly IReadOnlyList<string> FilePaths = new[] { "a.txt", "d/a.txt", "e/b.txt", "d/c/a.txt", "e/c/b.txt", "f/a.txt" };

    /// <summary>
    /// Makes a sequence from a seed. Each step is chosen so it can be made, assuming every earlier step passed as the model says.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <param name="maxOperations">The maximum number of operations.</param>
    /// <param name="maxBytes">The maximum length of one file.</param>
    /// <returns>The sequence that was made.</returns>
    public static DirectoryScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        DirectoryTree initial = CreateInitial(random, maxBytes);
        DirectoryTree model = initial.Clone();
        List<DirectoryOperation> operations = new List<DirectoryOperation>();
        int count = random.Next(1, maxOperations + 1);
        for (int i = 0; i < count; i++)
        {
            DirectoryOperation operation = Next(random, model, operations, maxBytes);
            operation.ApplyTo(model);
            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new DirectoryScenario(seed, initial, operations, commit);
    }

    /// <summary>
    /// Makes the tree before the start. d and e always exist.
    /// </summary>
    /// <param name="random">The random source that decides what to place.</param>
    /// <param name="maxBytes">The maximum file length.</param>
    /// <returns>The tree before the start.</returns>
    public static DirectoryTree CreateInitial(Random random, int maxBytes)
    {
        DirectoryTree initial = new DirectoryTree();
        foreach (string path in RootDirectories)
        {
            initial.AddDirectory(path);
        }

        foreach (string path in DirectoryPaths)
        {
            if (!initial.Contains(path) && initial.IsDirectory(DirectoryTree.Parent(path)) && random.Next(2) == 0)
            {
                initial.AddDirectory(path);
            }
        }

        foreach (string path in FilePaths)
        {
            if (initial.IsDirectory(DirectoryTree.Parent(path)) && random.Next(2) == 0)
            {
                initial.PutFile(path, StressContent.Create(random, maxBytes));
            }
        }

        return initial;
    }

    /// <summary>
    /// A readable form of the sequence for failure reports.
    /// </summary>
    /// <returns>A string with the starting tree, the operations, and how it ends.</returns>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();
        text.Append("seed=").Append(Seed).AppendLine();
        text.Append("initial dirs=[").Append(string.Join(", ", Initial.Directories.Order(StringComparer.Ordinal))).AppendLine("]");
        text.Append("initial files=[").Append(string.Join(", ", Initial.Files.Order(StringComparer.Ordinal).Select(path => path + " " + StressContent.Describe(Initial.File(path))))).AppendLine("]");
        for (int i = 0; i < Operations.Count; i++)
        {
            text.Append("  ").Append(i).Append(": ").Append(Operations[i]).AppendLine();
        }

        text.Append(Commit ? "  Commit" : "  Dispose");
        return text.ToString();
    }

    private static DirectoryOperation Next(
        Random random,
        DirectoryTree model,
        List<DirectoryOperation> applied,
        int maxBytes)
    {
        while (true)
        {
            DirectoryOperationKind kind = (DirectoryOperationKind)random.Next(7);
            DirectoryOperation operation = kind switch
            {
                DirectoryOperationKind.CreateDirectory => new DirectoryOperation(kind, Pick(random, DirectoryPaths), null, null),
                DirectoryOperationKind.Add => new DirectoryOperation(kind, Pick(random, FilePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Update => new DirectoryOperation(kind, Pick(random, FilePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Delete => new DirectoryOperation(kind, Pick(random, AllPaths()), null, null),
                DirectoryOperationKind.DeleteTree => new DirectoryOperation(kind, Pick(random, DirectoryPaths), null, null),
                DirectoryOperationKind.Move => new DirectoryOperation(kind, Pick(random, AllPaths()), Pick(random, AllPaths()), null),
                _ => new DirectoryOperation(DirectoryOperationKind.Read, Pick(random, FilePaths), null, null),
            };
            if (operation.CanApply(model, applied))
            {
                return operation;
            }
        }
    }

    private static IReadOnlyList<string> AllPaths()
    {
        List<string> paths = new List<string>();
        paths.AddRange(DirectoryPaths);
        paths.AddRange(FilePaths);
        return paths;
    }

    private static string Pick(Random random, IReadOnlyList<string> items) => items[random.Next(items.Count)];
}
