namespace Txfio;

/// <summary>
/// <see cref="Txfio.RecoverAsync(string, CancellationToken)"/> の結果
/// </summary>
public sealed class RecoverReport
{
    /// <summary>
    /// 全体の結果と、処理したジャーナルを指定する
    /// </summary>
    /// <param name="result">優先順位に従った全体の結果</param>
    /// <param name="journals">処理したジャーナル（生きているジャーナルは含めず、パスの大文字小文字を無視した辞書順）</param>
    public RecoverReport(RecoverResult result, IReadOnlyList<JournalReport> journals)
    {
        ArgumentNullException.ThrowIfNull(journals);
        Result = result;
        Journals = journals;
    }

    /// <summary>
    /// 優先順位に従った全体の結果
    /// </summary>
    public RecoverResult Result { get; }

    /// <summary>
    /// 処理したジャーナル（生きているジャーナルは含めず、パスの大文字小文字を無視した辞書順）
    /// </summary>
    public IReadOnlyList<JournalReport> Journals { get; }
}
