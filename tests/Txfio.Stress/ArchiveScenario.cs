using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 1 トランザクション分の ZIP 操作列と、開始時の木
/// </summary>
/// <param name="Seed">この列を作ったシード</param>
/// <param name="Initial">開始前にワークフォルダへ置く木</param>
/// <param name="ImportContent">外の in.zip に入れる a.txt の中身</param>
/// <param name="Operations">順に打つ操作</param>
/// <param name="Commit">最後に Commit するなら true、Dispose だけなら false</param>
internal sealed record ArchiveScenario(
    int Seed,
    DirectoryTree Initial,
    byte[] ImportContent,
    IReadOnlyList<ArchiveOperation> Operations,
    bool Commit)
{
    private static readonly string[] _sources = new[] { "d", "e", "a.txt", "d/a.txt" };

    private static readonly string[] _archives = new[] { "pack.zip", "a.zip", "b.zip", "c.zip" };

    private static readonly string[] _directories = new[] { "extracted", "imported", "copy", "more" };

    /// <summary>
    /// シードから操作列を作る。作成、展開、取り込み、書き出しを、通る手として先に入れる
    /// </summary>
    /// <param name="seed">シード</param>
    /// <param name="maxOperations">操作数の上限</param>
    /// <param name="maxBytes">1 ファイルの長さの上限</param>
    /// <returns>作った操作列</returns>
    public static ArchiveScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        DirectoryTree initial = CreateInitial(random, maxBytes);
        byte[] importContent = StressContent.Create(random, maxBytes);
        ArchiveWorld world = new ArchiveWorld(initial, importContent);
        List<ArchiveOperation> operations = new List<ArchiveOperation>();
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Create, "d", "pack.zip", false));
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Extract, "pack.zip", "extracted", false));
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Create, "a.txt", "a.zip", false));
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Import, "in.zip", "imported", false));
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Export, "d", world.NextExportPath(), false));
        TryAppend(world, operations, new ArchiveOperation(ArchiveKind.Export, "a.txt", world.NextExportPath(), true));

        int count = random.Next(operations.Count, Math.Max(operations.Count, maxOperations) + 1);
        for (int i = operations.Count; i < count; i++)
        {
            ArchiveOperation operation = Next(random, world);
            if (operation.CanApply(world))
            {
                operation.ApplyTo(world);
            }

            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new ArchiveScenario(seed, initial, importContent, operations, commit);
    }

    /// <summary>
    /// 失敗の報告に使う、読める形の操作列
    /// </summary>
    /// <returns>開始時の木、操作、終わり方を並べた文字列</returns>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();
        text.Append("seed=").Append(Seed).AppendLine();
        text.Append("initial dirs=[").Append(string.Join(", ", Initial.Directories.OrderBy(path => path, StringComparer.Ordinal))).AppendLine("]");
        text.Append("initial files=[").Append(string.Join(", ", Initial.Files.OrderBy(path => path, StringComparer.Ordinal).Select(path => path + " " + StressContent.Describe(Initial.File(path))))).AppendLine("]");
        text.Append("import=").Append(StressContent.Describe(ImportContent)).AppendLine();
        for (int i = 0; i < Operations.Count; i++)
        {
            text.Append("  ").Append(i).Append(": ").Append(Operations[i]).AppendLine();
        }

        text.Append(Commit ? "  Commit" : "  Dispose");
        return text.ToString();
    }

    private static DirectoryTree CreateInitial(Random random, int maxBytes)
    {
        DirectoryTree initial = new DirectoryTree();
        initial.AddDirectory("d");
        initial.AddDirectory("d/sub");
        initial.AddDirectory("d/empty");
        initial.AddDirectory("e");
        initial.PutFile("a.txt", StressContent.Create(random, maxBytes));
        initial.PutFile("d/a.txt", StressContent.Create(random, maxBytes));
        initial.PutFile("d/sub/b.txt", StressContent.Create(random, maxBytes));
        return initial;
    }

    private static void TryAppend(ArchiveWorld world, List<ArchiveOperation> operations, ArchiveOperation operation)
    {
        if (!operation.CanApply(world))
        {
            return;
        }

        operation.ApplyTo(world);
        operations.Add(operation);
    }

    private static ArchiveOperation Next(Random random, ArchiveWorld world)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            if (random.Next(4) == 0)
            {
                ArchiveOperation rejected = Reject(random);
                if (!rejected.CanApply(world))
                {
                    return rejected;
                }

                continue;
            }

            ArchiveOperation? legal = TryLegal(random, world);
            if (legal is not null)
            {
                return legal;
            }
        }

        return new ArchiveOperation(ArchiveKind.Create, "missing", "z.zip", false);
    }

    private static ArchiveOperation? TryLegal(Random random, ArchiveWorld world)
    {
        ArchiveKind kind = (ArchiveKind)random.Next(4);
        bool includeBase = random.Next(2) == 0;
        ArchiveOperation operation = kind switch
        {
            ArchiveKind.Create => new ArchiveOperation(kind, Pick(random, _sources), Pick(random, _archives), includeBase),
            ArchiveKind.Extract => new ArchiveOperation(kind, Pick(random, _archives), Pick(random, _directories), false),
            ArchiveKind.Import => new ArchiveOperation(kind, "in.zip", Pick(random, _directories), false),
            _ => new ArchiveOperation(kind, Pick(random, _sources), world.NextExportPath(), includeBase),
        };
        return operation.CanApply(world) ? operation : null;
    }

    private static ArchiveOperation Reject(Random random)
    {
        ArchiveOperation[] options = new ArchiveOperation[]
        {
            new ArchiveOperation(ArchiveKind.Create, "missing", "z.zip", false),
            new ArchiveOperation(ArchiveKind.Create, "d", "d", false),
            new ArchiveOperation(ArchiveKind.Create, "d", "d/in.zip", false),
            new ArchiveOperation(ArchiveKind.Create, "d", "nope/a.zip", false),
            new ArchiveOperation(ArchiveKind.Extract, "missing.zip", "copy", false),
            new ArchiveOperation(ArchiveKind.Extract, "pack.zip", "d", false),
            new ArchiveOperation(ArchiveKind.Import, "missing.zip", "copy", false),
            new ArchiveOperation(ArchiveKind.Export, "d", "out", false),
            new ArchiveOperation(ArchiveKind.Export, "missing", "out/z.zip", false),
            new ArchiveOperation(ArchiveKind.Export, "d", "gone/a.zip", false),
        };
        return options[random.Next(options.Length)];
    }

    private static string Pick(Random random, IReadOnlyList<string> paths)
    {
        return paths[random.Next(paths.Count)];
    }
}
