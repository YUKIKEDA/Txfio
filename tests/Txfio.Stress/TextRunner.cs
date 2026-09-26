using System.Text.Json;
using Txfio.Tests.Support;

namespace Txfio.Tests.Stress;

/// <summary>
/// 文字列と JSON の列を本物のトランザクションとメモリ上のテキストの両方に打ち、食い違いを探す
/// </summary>
internal static class TextRunner
{
    private const int MaxShrinkRuns = 300;

    /// <summary>
    /// 操作列を 1 回実行し、約束が破れていればその説明を返す
    /// </summary>
    /// <param name="scenario">実行する操作列</param>
    /// <returns>約束が守られていれば null、破れていればその説明</returns>
    public static async Task<string?> RunAsync(TextScenario scenario)
    {
        await using TempDirectory work = TempDirectory.Create();
        await WriteModelAsync(work.Path, scenario.Initial);
        TextModel model = scenario.Initial.Clone();
        System.Text.StringBuilder trace = new System.Text.StringBuilder();
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
    public static async Task<(TextScenario Scenario, string Failure)> ShrinkAsync(TextScenario scenario, string failure)
    {
        int runs = 0;
        bool shrunk = true;
        while (shrunk && runs < MaxShrinkRuns)
        {
            shrunk = false;
            for (int i = 0; i < scenario.Operations.Count && runs < MaxShrinkRuns; i++)
            {
                List<TextOperation> operations = scenario.Operations.ToList();
                operations.RemoveAt(i);
                TextScenario candidate = scenario with { Operations = operations };
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
        TextScenario scenario,
        TextModel model,
        System.Text.StringBuilder trace)
    {
        ITransaction tx = await global::Txfio.Txfio.BeginAsync(workFolder);
        try
        {
            for (int i = 0; i < scenario.Operations.Count; i++)
            {
                TextOperation operation = scenario.Operations[i];
                if (!operation.CanApply(model))
                {
                    try
                    {
                        await ApplyAsync(tx, operation);
                        return "手 " + i + " は拒否されるはずだったが通った: " + operation;
                    }
                    catch (Exception exception) when (exception is ExternalConflictException or UnsupportedOperationException or InvalidOperationException)
                    {
                        trace.Append("  ").Append(i).Append(": rejected ").Append(operation)
                            .Append(" (").Append(exception.Message).Append(')').AppendLine();
                    }
                }
                else
                {
                    try
                    {
                        string? mismatch = await ApplyAndCompareAsync(tx, model, operation);
                        trace.Append("  ").Append(i).Append(": ok ").Append(operation).AppendLine();
                        if (mismatch is not null)
                        {
                            return "手 " + i + " の読み戻しがモデルと違う: " + mismatch;
                        }
                    }
                    catch (Exception exception) when (exception is ExternalConflictException or UnsupportedOperationException or InvalidOperationException or JsonException)
                    {
                        return "手 " + i + " は通るはずだったが失敗した: " + operation + " (" + exception.Message + ")";
                    }
                }
            }

            TextModel expected = scenario.Initial;
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

    private static async Task<string?> ApplyAndCompareAsync(ITransaction tx, TextModel model, TextOperation operation)
    {
        switch (operation.Kind)
        {
            case TextKind.ReadText:
                {
                    string actual = await tx.ReadAllTextAsync(operation.Path);
                    return SameText(operation.Path, model.Text(operation.Path), actual);
                }

            case TextKind.ReadLines:
                {
                    string[] actual = await tx.ReadAllLinesAsync(operation.Path);
                    return SameLines(operation.Path, TextModel.SplitLines(model.Text(operation.Path)), actual);
                }

            case TextKind.ReadJson:
                {
                    StressJsonValue? actual = await tx.ReadFromJsonAsync<StressJsonValue>(operation.Path);
                    if (actual is null || !actual.Equals(operation.Json))
                    {
                        return operation.Path + " の JSON が書いた値と違う";
                    }

                    return null;
                }

            default:
                await ApplyAsync(tx, operation);
                operation.ApplyTo(model);
                string? text = await CompareTextAsync(tx, model, operation.Path);
                if (text is not null)
                {
                    return text;
                }

                if (operation.Kind == TextKind.WriteJson)
                {
                    StressJsonValue? actual = await tx.ReadFromJsonAsync<StressJsonValue>(operation.Path);
                    if (actual is null || !actual.Equals(operation.Json))
                    {
                        return operation.Path + " の JSON が書いた値と違う";
                    }
                }

                return null;
        }
    }

    private static async Task ApplyAsync(ITransaction tx, TextOperation operation)
    {
        switch (operation.Kind)
        {
            case TextKind.WriteText:
                await tx.WriteAllTextAsync(operation.Path, operation.Text);
                break;
            case TextKind.WriteLines:
                await tx.WriteAllLinesAsync(operation.Path, operation.Lines!);
                break;
            case TextKind.AppendText:
                await tx.AppendAllTextAsync(operation.Path, operation.Text);
                break;
            case TextKind.AppendLines:
                await tx.AppendAllLinesAsync(operation.Path, operation.Lines!);
                break;
            case TextKind.ReadText:
                await tx.ReadAllTextAsync(operation.Path);
                break;
            case TextKind.ReadLines:
                await tx.ReadAllLinesAsync(operation.Path);
                break;
            case TextKind.WriteJson:
                await tx.WriteAsJsonAsync(operation.Path, operation.Json);
                break;
            default:
                await tx.ReadFromJsonAsync<StressJsonValue>(operation.Path);
                break;
        }
    }

    private static async Task<string?> CompareTextAsync(ITransaction tx, TextModel model, string path)
    {
        string actual = await tx.ReadAllTextAsync(path);
        string? text = SameText(path, model.Text(path), actual);
        if (text is not null)
        {
            return text;
        }

        string[] lines = await tx.ReadAllLinesAsync(path);
        return SameLines(path, TextModel.SplitLines(model.Text(path)), lines);
    }

    private static string? SameText(string path, string expected, string actual)
    {
        if (expected == actual)
        {
            return null;
        }

        return path + " は期待 " + expected.Length + " 文字、実際 " + actual.Length + " 文字";
    }

    private static string? SameLines(string path, string[] expected, string[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return path + " の行数は期待 " + expected.Length + "、実際 " + actual.Length;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return path + " の " + i + " 行目が違う";
            }
        }

        return null;
    }

    private static async Task WriteModelAsync(string workFolder, TextModel model)
    {
        foreach (string directory in model.Directories.OrderBy(path => Depth(path)).ThenBy(path => path, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(FullPath(workFolder, directory));
        }

        foreach (string file in model.Files)
        {
            await File.WriteAllBytesAsync(FullPath(workFolder, file), TextModel.Encode(model.Text(file)));
        }
    }

    private static async Task<string?> CompareDiskAsync(string workFolder, TextModel expected)
    {
        foreach (string directory in expected.Directories)
        {
            if (!Directory.Exists(FullPath(workFolder, directory)))
            {
                return "終わったあとにディレクトリが無い: " + directory;
            }
        }

        foreach (string file in expected.Files)
        {
            string full = FullPath(workFolder, file);
            if (!File.Exists(full))
            {
                return "終わったあとにファイルが無い: " + file;
            }

            byte[] actual = await File.ReadAllBytesAsync(full);
            byte[] encoded = TextModel.Encode(expected.Text(file));
            if (!encoded.AsSpan().SequenceEqual(actual))
            {
                return "終わったあとのファイルが違う: " + file + " は期待 " + encoded.Length + " バイト、実際 " + actual.Length + " バイト";
            }
        }

        foreach (string file in Directory.EnumerateFiles(workFolder, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(workFolder, file).Replace('\\', '/');
            if (relative.StartsWith(".txfio/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!expected.IsFile(relative))
            {
                return "終わったあとにファイルが余分: " + relative;
            }
        }

        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        if (Directory.Exists(metadata) && Directory.EnumerateFiles(metadata, "tx-*.journal").Any())
        {
            return "終わったあとにジャーナルが残った";
        }

        return null;
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
