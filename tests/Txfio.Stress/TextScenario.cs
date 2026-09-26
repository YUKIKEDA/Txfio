using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 1 トランザクション分の文字列と JSON の操作列と、開始時のテキスト
/// </summary>
/// <param name="Seed">この列を作ったシード</param>
/// <param name="Initial">開始前にディスクへ置くテキスト</param>
/// <param name="Operations">順に打つ操作</param>
/// <param name="Commit">最後に Commit するなら true、Dispose だけなら false</param>
internal sealed record TextScenario(
    int Seed,
    TextModel Initial,
    IReadOnlyList<TextOperation> Operations,
    bool Commit)
{
    private static readonly string[] _paths = new[] { "a.txt", "b.txt", "c.txt", "d/a.txt", "e/b.txt", "j.txt" };

    /// <summary>
    /// シードから操作列を作る。各 API を、通る手として先に入れる
    /// </summary>
    /// <param name="seed">シード</param>
    /// <param name="maxOperations">操作数の上限</param>
    /// <param name="maxBytes">1 ファイルの長さの上限。テキストは既定では数十 KB まで</param>
    /// <returns>作った操作列</returns>
    public static TextScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        TextModel initial = CreateInitial(random, maxBytes);
        TextModel model = initial.Clone();
        List<TextOperation> operations = new List<TextOperation>();
        TryAppend(model, operations, new TextOperation(TextKind.WriteText, "c.txt", StressContent.CreateText(random, maxBytes), null, null));
        TryAppend(model, operations, new TextOperation(TextKind.WriteLines, "d/a.txt", null, CreateLines(random, maxBytes), null));
        TryAppend(model, operations, new TextOperation(TextKind.AppendText, "c.txt", StressContent.CreateText(random, maxBytes), null, null));
        TryAppend(model, operations, new TextOperation(TextKind.AppendLines, "a.txt", null, CreateLines(random, maxBytes), null));
        TryAppend(model, operations, new TextOperation(TextKind.ReadText, "a.txt", null, null, null));
        TryAppend(model, operations, new TextOperation(TextKind.ReadLines, "c.txt", null, null, null));
        StressJsonValue json = CreateJson(random);
        TryAppend(model, operations, new TextOperation(TextKind.WriteJson, "j.txt", null, null, json));
        TryAppend(model, operations, new TextOperation(TextKind.ReadJson, "j.txt", null, null, json));

        int count = random.Next(operations.Count, Math.Max(operations.Count, maxOperations) + 1);
        for (int i = operations.Count; i < count; i++)
        {
            TextOperation operation = Next(random, model, maxBytes);
            if (operation.CanApply(model))
            {
                operation.ApplyTo(model);
            }

            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new TextScenario(seed, initial, operations, commit);
    }

    /// <summary>
    /// 失敗の報告に使う、読める形の操作列
    /// </summary>
    /// <returns>開始時のファイル、操作、終わり方を並べた文字列</returns>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();
        text.Append("seed=").Append(Seed).AppendLine();
        text.Append("initial files=[").Append(string.Join(", ", Initial.Files.OrderBy(path => path, StringComparer.Ordinal).Select(path => path + " " + Initial.Text(path).Length + " 文字"))).AppendLine("]");
        for (int i = 0; i < Operations.Count; i++)
        {
            text.Append("  ").Append(i).Append(": ").Append(Operations[i]).AppendLine();
        }

        text.Append(Commit ? "  Commit" : "  Dispose");
        return text.ToString();
    }

    private static TextModel CreateInitial(Random random, int maxBytes)
    {
        TextModel initial = new TextModel();
        initial.AddDirectory("d");
        initial.AddDirectory("e");
        initial.PutFile("a.txt", StressContent.CreateText(random, maxBytes));
        foreach (string path in new[] { "b.txt", "d/a.txt", "e/b.txt" })
        {
            if (initial.ParentIsDirectory(path) && random.Next(2) == 0)
            {
                initial.PutFile(path, StressContent.CreateText(random, maxBytes));
            }
        }

        return initial;
    }

    private static void TryAppend(TextModel model, List<TextOperation> operations, TextOperation operation)
    {
        if (!operation.CanApply(model))
        {
            return;
        }

        operation.ApplyTo(model);
        operations.Add(operation);
    }

    private static TextOperation Next(Random random, TextModel model, int maxBytes)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            if (random.Next(5) == 0)
            {
                TextOperation rejected = Reject(random);
                if (!rejected.CanApply(model))
                {
                    return rejected;
                }

                continue;
            }

            TextOperation operation = Legal(random, model, maxBytes);
            if (operation.CanApply(model))
            {
                return operation;
            }
        }

        return new TextOperation(TextKind.ReadText, "missing.txt", null, null, null);
    }

    private static TextOperation Legal(Random random, TextModel model, int maxBytes)
    {
        string path = Pick(random, _paths);
        TextKind kind = (TextKind)random.Next(8);
        if (kind == TextKind.ReadJson || kind == TextKind.WriteJson)
        {
            StressJsonValue json = CreateJson(random);
            if (kind == TextKind.ReadJson)
            {
                foreach (string file in model.Files)
                {
                    if (model.IsJson(file, json))
                    {
                        return new TextOperation(TextKind.ReadJson, file, null, null, json);
                    }
                }

                return new TextOperation(TextKind.ReadText, path, null, null, null);
            }

            return new TextOperation(TextKind.WriteJson, path, null, null, json);
        }

        if (kind == TextKind.WriteLines || kind == TextKind.AppendLines)
        {
            return new TextOperation(kind, path, null, CreateLines(random, maxBytes), null);
        }

        if (kind == TextKind.WriteText || kind == TextKind.AppendText)
        {
            return new TextOperation(kind, path, StressContent.CreateText(random, maxBytes), null, null);
        }

        return new TextOperation(kind, path, null, null, null);
    }

    private static TextOperation Reject(Random random)
    {
        TextOperation[] options = new TextOperation[]
        {
            new TextOperation(TextKind.ReadText, "missing.txt", null, null, null),
            new TextOperation(TextKind.ReadLines, "d", null, null, null),
            new TextOperation(TextKind.WriteText, "d", "x", null, null),
            new TextOperation(TextKind.AppendText, "nope/a.txt", "x", null, null),
            new TextOperation(TextKind.WriteJson, "e", null, null, new StressJsonValue(1, "a", true)),
        };
        return options[random.Next(options.Length)];
    }

    private static StressJsonValue CreateJson(Random random)
    {
        return new StressJsonValue(random.Next(), StressContent.CreateText(random, 32), random.Next(2) == 0);
    }

    private static string[] CreateLines(Random random, int maxBytes)
    {
        string text = StressContent.CreateText(random, maxBytes);
        List<string> lines = new List<string>();
        for (int i = 0; i < text.Length; i += 32)
        {
            int length = Math.Min(32, text.Length - i);
            lines.Add(text.Substring(i, length));
        }

        return lines.ToArray();
    }

    private static string Pick(Random random, IReadOnlyList<string> paths)
    {
        return paths[random.Next(paths.Count)];
    }
}
