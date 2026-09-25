namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列を比べるメモリ上のモデル。「コミット後の姿」を持つ
/// </summary>
internal sealed class RandomOperationModel
{
    private readonly Dictionary<string, string> _files;

    /// <summary>
    /// 開始時のファイルからモデルを作る
    /// </summary>
    /// <param name="initialFiles">相対パスから内容への辞書</param>
    public RandomOperationModel(IReadOnlyDictionary<string, string> initialFiles)
    {
        _files = new Dictionary<string, string>(initialFiles, StringComparer.Ordinal);
    }

    /// <summary>
    /// コミットしたときにあるはずのファイル（相対パスから内容）。ReadAsync もこの内容を返す
    /// </summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>
    /// 通った操作をモデルに反映する
    /// </summary>
    /// <param name="operation">通った操作</param>
    public void Apply(RandomOperation operation)
    {
        operation.ApplyTo(_files);
    }
}
