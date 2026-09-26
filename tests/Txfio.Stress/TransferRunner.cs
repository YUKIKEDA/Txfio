using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// コピー、取り込み、書き出しの列を本物のトランザクションとメモリ上の木の両方に打ち、食い違いを探す
/// </summary>
internal static class TransferRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// 操作列を 1 回実行し、約束が破れていればその説明を返す
    /// </summary>
    /// <param name="scenario">実行する操作列</param>
    /// <returns>約束が守られていれば null、破れていればその説明</returns>
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
                        return "手 " + i + " は拒否されるはずだったが通った: " + operation;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ExternalConflictException)
                    {
                        trace.Append("  ").Append(i).Append(": rejected ").Append(operation)
                            .Append(" (").Append(exception.Message).Append(')').AppendLine();
                        string pendingAfter = DescribePending(tx);
                        if (pendingAfter != pendingBefore)
                        {
                            return "手 " + i + " は拒否されたのに予約が変わった: 前 [" + pendingBefore + "]、後 [" + pendingAfter + "]";
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
                        return "手 " + i + " は通るはずだったが拒否された: " + operation + " (" + exception.Message + ")";
                    }
                }

                string? mismatch = await CompareViewAsync(workFolder, tx, world.Commit);
                if (mismatch is not null)
                {
                    return "手 " + i + " のあとの姿がモデルと違う: " + mismatch;
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
                return "書き出しがコピー時点の外と違う: " + exported;
            }

            await global::Txfio.Txfio.RecoverAsync(workFolder);
            string? afterRecover = await CompareDiskAsync(outsideRoot, world.ExpectedOutside());
            if (afterRecover is not null)
            {
                return "RecoverAsync のあと書き出しが消えたか変わった: " + afterRecover;
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
                return "終わったあとにディレクトリが無い: " + directory;
            }
        }

        foreach (string directory in actual.Directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expected.IsDirectory(directory))
            {
                return "終わったあとにディレクトリが余分: " + directory;
            }
        }

        foreach (string file in expected.Files.OrderBy(path => path, StringComparer.Ordinal))
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

        foreach (string file in actual.Files.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expected.IsFile(file))
            {
                return "終わったあとにファイルが余分: " + file + "（" + StressContent.Describe(actual.File(file)) + "）";
            }
        }

        string metadata = System.IO.Path.Combine(root, ".txfio");
        if (Directory.Exists(metadata) && Directory.EnumerateFiles(metadata, "tx-*.journal").Any())
        {
            return "終わったあとにジャーナルが残った";
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
