namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列を比べるメモリ上のモデル
/// </summary>
internal sealed class RandomOperationModel
{
    private readonly Dictionary<string, string> _files;

    private readonly HashSet<string> _moveDestinations = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 開始時のファイルからモデルを作る
    /// </summary>
    /// <param name="initialFiles">相対パスから内容への辞書</param>
    public RandomOperationModel(IReadOnlyDictionary<string, string> initialFiles)
    {
        _files = new Dictionary<string, string>(initialFiles, StringComparer.Ordinal);
    }

    /// <summary>
    /// コミットしたときにあるはずのファイル（相対パスから内容）
    /// </summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>
    /// 内容が Move だけで来たパスか。そのパスは .txnew も本物も無いので、コミット前の ReadAsync で読めなくてよい
    /// </summary>
    /// <param name="path">相対パス</param>
    /// <returns>Move の移動先のままなら true</returns>
    public bool IsMoveDestination(string path) => _moveDestinations.Contains(path);

    /// <summary>
    /// 通った操作をモデルに反映する
    /// </summary>
    /// <param name="operation">通った操作</param>
    public void Apply(RandomOperation operation)
    {
        operation.ApplyTo(_files);
        switch (operation.Kind)
        {
            case RandomOperationKind.Move:
                _moveDestinations.Remove(operation.Path);
                _moveDestinations.Add(operation.NewPath!);
                break;
            case RandomOperationKind.Read:
                break;
            default:
                _moveDestinations.Remove(operation.Path);
                break;
        }
    }
}
