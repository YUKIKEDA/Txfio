using System.Text;

namespace Txfio.Tests.Stress;

/// <summary>
/// 1 トランザクション分のランダム操作列と、開始時のファイル
/// </summary>
/// <param name="Seed">この列を作ったシード</param>
/// <param name="InitialFiles">開始前にディスクへ置くファイル（相対パスからバイト列）</param>
/// <param name="Operations">順に打つ操作</param>
/// <param name="Commit">最後に Commit するなら true、Dispose だけなら false</param>
internal sealed record RandomOperationScenario(
    int Seed,
    IReadOnlyDictionary<string, byte[]> InitialFiles,
    IReadOnlyList<RandomOperation> Operations,
    bool Commit)
{
    /// <summary>
    /// 操作の対象にするパス。ルートと、いつもある <c>sub</c> の下
    /// </summary>
    public static readonly IReadOnlyList<string> Paths = new[] { "a.txt", "b.txt", "c.txt", "sub/a.txt", "sub/b.txt" };

    /// <summary>
    /// いつもあるサブディレクトリ
    /// </summary>
    public const string SubDirectory = "sub";

    /// <summary>
    /// シードから操作列を作る。各手は、それまでの手がすべてモデルどおり通った前提で打てるものを選ぶ
    /// </summary>
    /// <param name="seed">シード</param>
    /// <param name="maxOperations">操作数の上限</param>
    /// <param name="maxBytes">1 ファイルの長さの上限</param>
    /// <returns>作った操作列</returns>
    public static RandomOperationScenario Generate(int seed, int maxOperations, int maxBytes)
    {
        Random random = new Random(seed);
        Dictionary<string, byte[]> initial = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in Paths)
        {
            if (random.Next(2) == 0)
            {
                initial[path] = StressContent.Create(random, maxBytes);
            }
        }

        Dictionary<string, byte[]> model = new Dictionary<string, byte[]>(initial, StringComparer.Ordinal);
        List<RandomOperation> operations = new List<RandomOperation>();
        int count = random.Next(1, maxOperations + 1);
        for (int i = 0; i < count; i++)
        {
            RandomOperation operation = Next(random, model, maxBytes);
            operation.ApplyTo(model);
            operations.Add(operation);
        }

        bool commit = random.Next(5) != 0;
        return new RandomOperationScenario(seed, initial, operations, commit);
    }

    /// <summary>
    /// 失敗の報告に使う、読める形の操作列
    /// </summary>
    /// <returns>開始時のファイル、操作、終わり方を並べた文字列</returns>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();
        text.Append("seed=").Append(Seed).AppendLine();
        text.Append("initial=[").Append(string.Join(", ", InitialFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + StressContent.Describe(pair.Value)))).AppendLine("]");
        for (int i = 0; i < Operations.Count; i++)
        {
            text.Append("  ").Append(i).Append(": ").Append(Operations[i]).AppendLine();
        }

        text.Append(Commit ? "  Commit" : "  Dispose");
        return text.ToString();
    }

    private static RandomOperation Next(Random random, Dictionary<string, byte[]> model, int maxBytes)
    {
        List<string> present = Paths.Where(model.ContainsKey).ToList();
        List<string> absent = Paths.Where(path => !model.ContainsKey(path)).ToList();
        while (true)
        {
            switch (random.Next(5))
            {
                case 0 when absent.Count > 0:
                    return new RandomOperation(RandomOperationKind.Add, Pick(random, absent), null, StressContent.Create(random, maxBytes));
                case 1 when present.Count > 0:
                    return new RandomOperation(RandomOperationKind.Update, Pick(random, present), null, StressContent.Create(random, maxBytes));
                case 2 when present.Count > 0:
                    return new RandomOperation(RandomOperationKind.Delete, Pick(random, present), null, null);
                case 3 when present.Count > 0 && absent.Count > 0:
                    return new RandomOperation(RandomOperationKind.Move, Pick(random, present), Pick(random, absent), null);
                case 4 when present.Count > 0:
                    return new RandomOperation(RandomOperationKind.Read, Pick(random, present), null, null);
            }
        }
    }

    private static string Pick(Random random, List<string> items) => items[random.Next(items.Count)];
}
