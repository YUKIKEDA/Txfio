using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// ディレクトリの操作列を本物のトランザクションとメモリ上の木の両方に打ち、食い違いを探す
/// </summary>
internal static class DirectoryRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// 操作列を 1 回実行し、約束が破れていればその説明を返す
    /// </summary>
    /// <param name="scenario">実行する操作列</param>
    /// <returns>約束が守られていれば null、破れていればその説明</returns>
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
                            return $"手 {i} の ReadAsync がモデルと違う: {mismatchRead}";
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
                        return $"手 {i} は拒否されたのに予約が変わった: 前 [{pendingBefore}]、後 [{pendingAfter}]";
                    }
                }

                string? mismatch = await CompareViewAsync(workFolder, tx, model);
                if (mismatch is not null)
                {
                    return $"手 {i} のあとの姿がモデルと違う: {mismatch}";
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
                        return "外から変えていないのに PartialConflict になった";
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
                await tx.MoveAsync(operation.Path, operation.NewPath!);
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
            return path + " は長さ " + StressContent.Describe(expected) + " で内容が違う";
        }

        return path + " は期待 " + StressContent.Describe(expected) + "、実際 " + StressContent.Describe(actual);
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
            return directory + " の直下の件数が違う: 期待 " + expected.Count + "、実際 " + actual.Count;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual[i].Path != expected[i].Path || actual[i].IsDirectory != expected[i].IsDirectory)
            {
                return directory + " の直下が違う: 期待 " + expected[i].Path + "、実際 " + actual[i].Path;
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
                return "終わったあとにディレクトリが無い: " + directory;
            }
        }

        foreach (string directory in actual.Directories.Order(StringComparer.Ordinal))
        {
            if (!expected.IsDirectory(directory))
            {
                return "終わったあとにディレクトリが余分: " + directory;
            }
        }

        foreach (string file in expected.Files.Order(StringComparer.Ordinal))
        {
            if (!actual.IsFile(file))
            {
                return "終わったあとにファイルが無い: " + file + "（" + StressContent.Describe(expected.File(file)) + "）";
            }

            byte[] found = actual.File(file);
            if (!expected.File(file).AsSpan().SequenceEqual(found))
            {
                return "終わったあとのファイルが違う: " + file + " は期待 " + StressContent.Describe(expected.File(file)) + "、実際 " + StressContent.Describe(found);
            }
        }

        foreach (string file in actual.Files.Order(StringComparer.Ordinal))
        {
            if (!expected.IsFile(file))
            {
                return "終わったあとにファイルが余分: " + file + "（" + StressContent.Describe(actual.File(file)) + "）";
            }
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
