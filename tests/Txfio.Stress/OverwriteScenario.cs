namespace Txfio.Tests.Stress;

/// <summary>
/// 移動先を置き換える Move を含む操作列を作る
/// </summary>
internal static class OverwriteScenario
{
    /// <summary>
    /// シードから操作列を作る。最初の 1 手は、置けるなら置き換えの Move にする
    /// </summary>
    /// <param name="seed">シード</param>
    /// <param name="maxOperations">操作数の上限</param>
    /// <param name="maxBytes">1 ファイルの長さの上限</param>
    /// <returns>作った操作列</returns>
    public static DirectoryScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        DirectoryTree initial = DirectoryScenario.CreateInitial(random, maxBytes);
        DirectoryTree model = initial.Clone();
        List<DirectoryOperation> operations = new List<DirectoryOperation>();
        DirectoryOperation? first = TryOverwrite(random, model, operations);
        if (first is not null)
        {
            first.ApplyTo(model);
            operations.Add(first);
        }

        int count = random.Next(operations.Count, Math.Max(operations.Count, maxOperations) + 1);
        for (int i = operations.Count; i < count; i++)
        {
            DirectoryOperation? operation = Next(random, model, operations, maxBytes);
            if (operation is null)
            {
                break;
            }

            operation.ApplyTo(model);
            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new DirectoryScenario(seed, initial, operations, commit);
    }

    private static DirectoryOperation? Next(
        Random random,
        DirectoryTree model,
        List<DirectoryOperation> applied,
        int maxBytes)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            if (random.Next(3) == 0)
            {
                DirectoryOperation? overwrite = TryOverwrite(random, model, applied);
                if (overwrite is not null)
                {
                    return overwrite;
                }
            }

            int kindIndex = random.Next(5);
            DirectoryOperationKind kind = kindIndex switch
            {
                0 => DirectoryOperationKind.CreateDirectory,
                1 => DirectoryOperationKind.Add,
                2 => DirectoryOperationKind.Update,
                3 => DirectoryOperationKind.Delete,
                _ => DirectoryOperationKind.Read,
            };
            DirectoryOperation operation = kind switch
            {
                DirectoryOperationKind.CreateDirectory => new DirectoryOperation(kind, Pick(random, DirectoryScenario.DirectoryPaths), null, null),
                DirectoryOperationKind.Add => new DirectoryOperation(kind, Pick(random, DirectoryScenario.FilePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Update => new DirectoryOperation(kind, Pick(random, DirectoryScenario.FilePaths), null, StressContent.Create(random, maxBytes)),
                DirectoryOperationKind.Delete => new DirectoryOperation(kind, Pick(random, AllPaths()), null, null),
                _ => new DirectoryOperation(DirectoryOperationKind.Read, Pick(random, DirectoryScenario.FilePaths), null, null),
            };
            if (operation.CanApply(model, applied))
            {
                return operation;
            }
        }

        return null;
    }

    private static DirectoryOperation? TryOverwrite(
        Random random,
        DirectoryTree model,
        List<DirectoryOperation> applied)
    {
        IReadOnlyList<string> paths = AllPaths();
        for (int attempt = 0; attempt < paths.Count * paths.Count; attempt++)
        {
            DirectoryOperation operation = new DirectoryOperation(
                DirectoryOperationKind.Move,
                Pick(random, paths),
                Pick(random, paths),
                null,
                Overwrite: true);
            if (operation.CanApply(model, applied))
            {
                return operation;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> AllPaths()
    {
        List<string> paths = new List<string>();
        paths.AddRange(DirectoryScenario.DirectoryPaths);
        paths.AddRange(DirectoryScenario.FilePaths);
        return paths;
    }

    private static string Pick(Random random, IReadOnlyList<string> items) => items[random.Next(items.Count)];
}
