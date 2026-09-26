namespace Txfio.Tests.Stress;

/// <summary>
/// コピー、取り込み、書き出しの 1 手
/// </summary>
/// <param name="Kind">操作の種類</param>
/// <param name="Source">コピー元。Copy と Export はワークフォルダ基準、Import は外の相対パス</param>
/// <param name="Destination">コピー先。Copy と Import はワークフォルダ基準、Export は外の相対パス</param>
internal sealed record TransferOperation(TransferKind Kind, string Source, string Destination)
{
    /// <summary>
    /// いまの姿でこの手を打てるか
    /// </summary>
    /// <param name="world">ワークフォルダと外の姿</param>
    /// <returns>打てるとき true</returns>
    public bool CanApply(TransferWorld world)
    {
        return world.CanApply(this);
    }

    /// <summary>
    /// メモリ上の姿へこの手を反映する
    /// </summary>
    /// <param name="world">ワークフォルダと外の姿</param>
    public void ApplyTo(TransferWorld world)
    {
        world.Apply(this);
    }

    /// <summary>
    /// 失敗の報告に使う、読める形
    /// </summary>
    /// <returns>種類とパス</returns>
    public override string ToString()
    {
        return Kind + "(" + Source + " -> " + Destination + ")";
    }
}
