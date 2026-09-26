namespace Txfio.Tests.Stress;

/// <summary>
/// ZIP の 1 手
/// </summary>
/// <param name="Kind">操作の種類</param>
/// <param name="Source">入力。Import は外の相対パス、それ以外はワークフォルダ基準</param>
/// <param name="Destination">出力。Export は外の相対パス、それ以外はワークフォルダ基準</param>
/// <param name="IncludeBase">ディレクトリを ZIP にするとき、その名前をエントリの先頭に含めるか</param>
internal sealed record ArchiveOperation(ArchiveKind Kind, string Source, string Destination, bool IncludeBase)
{
    /// <summary>
    /// いまの姿でこの手を打てるか
    /// </summary>
    /// <param name="world">ワークフォルダと外の姿</param>
    /// <returns>打てるとき true</returns>
    public bool CanApply(ArchiveWorld world)
    {
        return world.CanApply(this);
    }

    /// <summary>
    /// メモリ上の姿へこの手を反映する
    /// </summary>
    /// <param name="world">ワークフォルダと外の姿</param>
    public void ApplyTo(ArchiveWorld world)
    {
        world.Apply(this);
    }

    /// <summary>
    /// 失敗の報告に使う、読める形
    /// </summary>
    /// <returns>種類とパス</returns>
    public override string ToString()
    {
        return Kind + "(" + Source + " -> " + Destination + (IncludeBase ? ", base" : string.Empty) + ")";
    }
}
