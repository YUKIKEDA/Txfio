using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 1 トランザクション分のディレクトリ操作列と、開始時の木
/// </summary>
/// <param name="Seed">この列を作ったシード</param>
/// <param name="Initial">開始前にディスクへ置く木</param>
/// <param name="Operations">順に打つ操作</param>
/// <param name="Commit">最後に Commit するなら true、Dispose だけなら false</param>
internal sealed record DirectoryScenario(
    int Seed,
    DirectoryTree Initial,
    IReadOnlyList<DirectoryOperation> Operations,
    bool Commit)
{
    /// <summary>
    /// いつもあるディレクトリ
    /// </summary>
    public static readonly IReadOnlyList<string> RootDirectories = new[] { "d", "e" };

    private static readonly IReadOnlyList<string> _directoryPaths = new[] { "d", "e", "d/c", "e/c", "f" };

    private static readonly IReadOnlyList<string> _filePaths = new[] { "a.txt", "d/a.txt", "e/b.txt", "d/c/a.txt", "e/c/b.txt", "f/a.txt" };

    /// <summary>
    /// シードから操作列を作る。各手は、それまでの手がすべてモデルどおり通った前提で打てるものを選ぶ
    /// </summary>
    /// <param name="seed">シード</param>
    /// <param name="maxOperations">操作数の上限</param>
    /// <param name="maxBytes">1 ファイルの長さの上限</param>
    /// <returns>作った操作列</returns>
    public static DirectoryScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        DirectoryTree initial = new DirectoryTree();
        foreach (string path in RootDirectories)
        {
            initial.AddDirectory(path);
        }

        foreach (string path in _directoryPaths)
        {
            if (!initial.Contains(path) && initial.IsDirectory(DirectoryTree.Parent(path)) && random.Next(2) == 0)
            {
                initial.AddDirectory(path);
            }
        }

        foreach (string path in _filePaths)
        {
            if (initial.IsDirectory(DirectoryTree.Parent(path)) && random.Next(2) == 0)
            {
                initial.PutFile(path, StressContent.Create(random, maxBytes));
            }
        }

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
    /// 失敗の報告に使う、読める形の操作列
    /// </summary>
    /// <returns>開始時の木、操作、終わり方を並べた文字列</returns>
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
                DirectoryOperationKind.CreateDirectory => new DirectoryOperation(kind, Pick(random, _directoryPaths), null, null),
                DirectoryOperationKind.Add => new DirectoryOperation(kind, Pick(random, _filePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Update => new DirectoryOperation(kind, Pick(random, _filePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Delete => new DirectoryOperation(kind, Pick(random, AllPaths()), null, null),
                DirectoryOperationKind.DeleteTree => new DirectoryOperation(kind, Pick(random, _directoryPaths), null, null),
                DirectoryOperationKind.Move => new DirectoryOperation(kind, Pick(random, AllPaths()), Pick(random, AllPaths()), null),
                _ => new DirectoryOperation(DirectoryOperationKind.Read, Pick(random, _filePaths), null, null),
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
        paths.AddRange(_directoryPaths);
        paths.AddRange(_filePaths);
        return paths;
    }

    private static string Pick(Random random, IReadOnlyList<string> items) => items[random.Next(items.Count)];
}
