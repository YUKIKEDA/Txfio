using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列を本物のトランザクションとメモリ上のモデルの両方に打ち、食い違いを探す
/// </summary>
internal static class RandomOperationRunner
{
    /// <summary>
    /// 縮小で試す実行の上限
    /// </summary>
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// 操作列を 1 回実行し、約束が破れていればその説明を返す
    /// </summary>
    /// <remarks>
    /// 各手は、モデルどおりに反映されるか、<see cref="InvalidOperationException"/> で拒否されるかのどちらかでなければならない。
    /// 拒否された手はモデルを変えず、予約の一覧と読める内容も変えない。拒否でモデルとずれた手のあとは、モデルで打てない手を飛ばす
    /// </remarks>
    /// <param name="scenario">実行する操作列</param>
    /// <returns>約束が守られていれば null、破れていればその説明</returns>
    public static async Task<string?> RunAsync(RandomOperationScenario scenario)
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, RandomOperationScenario.SubDirectory));
        foreach (KeyValuePair<string, byte[]> file in scenario.InitialFiles)
        {
            await File.WriteAllBytesAsync(FullPath(work.Path, file.Key), file.Value);
        }

        RandomOperationModel model = new RandomOperationModel(scenario.InitialFiles);
        StringBuilder trace = new StringBuilder();
        string? failure;
        try
        {
            failure = await RunOperationsAsync(work.Path, scenario, model, trace);
        }
        catch (Exception exception)
        {
            failure = "想定外の例外: " + exception;
        }

        return failure is null ? null : failure + Environment.NewLine + "実行した手:" + Environment.NewLine + trace;
    }

    /// <summary>
    /// 失敗した操作列から手を 1 つずつ外し、まだ失敗する最小の列まで縮める
    /// </summary>
    /// <param name="scenario">失敗した操作列</param>
    /// <param name="failure">その失敗の説明</param>
    /// <returns>縮めた操作列とその失敗の説明</returns>
    public static async Task<(RandomOperationScenario Scenario, string Failure)> ShrinkAsync(
        RandomOperationScenario scenario,
        string failure)
    {
        int runs = 0;
        bool shrunk = true;
        while (shrunk && runs < MaxShrinkRuns)
        {
            shrunk = false;
            for (int i = 0; i < scenario.Operations.Count && runs < MaxShrinkRuns; i++)
            {
                List<RandomOperation> operations = scenario.Operations.ToList();
                operations.RemoveAt(i);
                RandomOperationScenario candidate = scenario with { Operations = operations };
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
        RandomOperationScenario scenario,
        RandomOperationModel model,
        StringBuilder trace)
    {
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
        try
        {
            for (int i = 0; i < scenario.Operations.Count; i++)
            {
                RandomOperation operation = scenario.Operations[i];
                if (!operation.CanApply(model.Files))
                {
                    trace.Append("  ").Append(i).Append(": skip ").Append(operation).AppendLine();
                    continue;
                }

                string pendingBefore = DescribePending(tx);
                try
                {
                    if (operation.Kind == RandomOperationKind.Read)
                    {
                        string? mismatchRead = await CompareReadAsync(tx, model, operation.Path);
                        trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                        if (mismatchRead is not null)
                        {
                            return $"手 {i} の ReadAsync がモデルと違う: {mismatchRead}";
                        }

                        continue;
                    }

                    await ApplyAsync(tx, operation);
                    trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                    model.Apply(operation);
                }
                catch (InvalidOperationException exception)
                {
                    trace.Append("  ").Append(i).Append(": rejected ").Append(operation)
                        .Append(" (").Append(exception.Message).Append(')').AppendLine();
                    string pendingAfter = DescribePending(tx);
                    if (pendingAfter != pendingBefore)
                    {
                        return $"手 {i} は拒否されたのに予約が変わった: 前 [{pendingBefore}]、後 [{pendingAfter}]";
                    }
                }

                string? mismatch = await CompareReadsAsync(tx, model);
                if (mismatch is not null)
                {
                    return $"手 {i} のあとの ReadAsync がモデルと違う: {mismatch}";
                }
            }

            IReadOnlyDictionary<string, byte[]> expected = scenario.InitialFiles;
            if (scenario.Commit)
            {
                try
                {
                    CommitReport report = await tx.CommitAsync();
                    trace.Append("  Commit -> ").Append(report.Result).AppendLine();
                    if (report.Result == CommitResult.PartialConflict)
                    {
                        return "外から変えていないのに PartialConflict になった";
                    }

                    if (report.Result == CommitResult.Succeeded)
                    {
                        expected = model.Files;
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

    private static async Task ApplyAsync(ITransaction tx, RandomOperation operation)
    {
        switch (operation.Kind)
        {
            case RandomOperationKind.Add:
                await using (MemoryStream content = new MemoryStream(operation.Content!))
                {
                    await tx.AddAsync(operation.Path, content);
                }

                break;
            case RandomOperationKind.Update:
                await using (MemoryStream content = new MemoryStream(operation.Content!))
                {
                    await tx.UpdateAsync(operation.Path, content);
                }

                break;
            case RandomOperationKind.Delete:
                await tx.DeleteAsync(operation.Path);
                break;
            case RandomOperationKind.Move:
                await tx.MoveAsync(operation.Path, operation.NewPath!);
                break;
        }
    }

    private static async Task<string?> CompareReadsAsync(ITransaction tx, RandomOperationModel model)
    {
        foreach (string path in model.Files.Keys.Order(StringComparer.Ordinal))
        {
            string? mismatch = await CompareReadAsync(tx, model, path);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }

        return null;
    }

    private static async Task<string?> CompareReadAsync(ITransaction tx, RandomOperationModel model, string path)
    {
        byte[] expected = model.Files[path];
        byte[] actual = await ReadAsync(tx, path);
        return SameContent(expected, actual) ? null : Mismatch(path, expected, actual);
    }

    private static async Task<string?> CompareDiskAsync(string workFolder, IReadOnlyDictionary<string, byte[]> expected)
    {
        SortedDictionary<string, byte[]> actual = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(workFolder, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(workFolder, file).Replace('\\', '/');
            if (!relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                actual[relative] = await File.ReadAllBytesAsync(file);
            }
        }

        string? mismatch = FirstMismatch(expected, actual);
        if (mismatch is not null)
        {
            return "終わったあとのディスクが違う: " + mismatch;
        }

        string[] directories = Directory.EnumerateDirectories(workFolder)
            .Select(System.IO.Path.GetFileName)
            .Where(name => name != ".txfio")
            .Select(name => name!)
            .ToArray();
        if (directories.Length != 1 || directories[0] != RandomOperationScenario.SubDirectory)
        {
            return "終わったあとのディレクトリが違う: [" + string.Join(", ", directories) + "]";
        }

        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        if (Directory.Exists(metadata) && Directory.EnumerateFiles(metadata, "tx-*.journal").Any())
        {
            return "終わったあとにジャーナルが残った";
        }

        return null;
    }

    private static string DescribePending(ITransaction tx)
    {
        return string.Join(", ", tx.GetPendingChanges().Select(change => $"{change.Kind}:{change.Path}>{change.NewPath}"));
    }

    private static async Task<byte[]> ReadAsync(ITransaction tx, string path)
    {
        await using Stream stream = await tx.ReadAsync(path);
        using MemoryStream copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static string? FirstMismatch(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        foreach (KeyValuePair<string, byte[]> file in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(file.Key, out byte[]? found))
            {
                return file.Key + " が無い（期待 " + StressContent.Describe(file.Value) + "）";
            }

            if (!SameContent(file.Value, found))
            {
                return Mismatch(file.Key, file.Value, found);
            }
        }

        foreach (string path in actual.Keys.Order(StringComparer.Ordinal))
        {
            if (!expected.ContainsKey(path))
            {
                return path + " が余分（" + StressContent.Describe(actual[path]) + "）";
            }
        }

        return null;
    }

    private static bool SameContent(byte[] expected, byte[] actual) => expected.AsSpan().SequenceEqual(actual);

    private static string Mismatch(string path, byte[] expected, byte[] actual)
    {
        if (expected.Length == actual.Length)
        {
            return path + " は長さ " + StressContent.Describe(expected) + " で内容が違う";
        }

        return path + " は期待 " + StressContent.Describe(expected) + "、実際 " + StressContent.Describe(actual);
    }

    private static string FullPath(string workFolder, string relative)
    {
        return System.IO.Path.Combine(workFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
