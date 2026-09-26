namespace Txfio.Tests.Support;

/// <summary>
/// 進捗が届いた時点のジャーナルに、指定した名前が書いてあるかを記録する（同期で呼ばれる）
/// </summary>
internal sealed class JournalProbeProgress : IProgress<TransferProgress>
{
    private readonly string _workFolder;
    private readonly string _expected;

    /// <summary>
    /// 調べるワークフォルダと、ジャーナルにあるはずの名前を指定する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="expected">ジャーナルにあるはずのファイル名</param>
    internal JournalProbeProgress(string workFolder, string expected)
    {
        _workFolder = workFolder;
        _expected = expected;
    }

    /// <summary>
    /// 進捗が 1 回でも届いた
    /// </summary>
    internal bool Reported { get; private set; }

    /// <summary>
    /// 届いたすべての時点で、ジャーナルに名前が書いてあった
    /// </summary>
    internal bool AlwaysJournaled { get; private set; } = true;

    /// <inheritdoc />
    public void Report(TransferProgress value)
    {
        Reported = true;
        string metadata = System.IO.Path.Combine(_workFolder, ".txfio");
        string journal = Directory.GetFiles(metadata, "tx-*.journal").Single();
        string text = File.ReadAllText(journal);
        if (!text.Contains(_expected, StringComparison.OrdinalIgnoreCase))
        {
            AlwaysJournaled = false;
        }
    }
}
