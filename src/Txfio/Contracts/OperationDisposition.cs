namespace Txfio;

/// <summary>
/// 操作を確定できなかったときの成り行き
/// </summary>
public enum OperationDisposition
{
    /// <summary>
    /// 検証で拒んだ
    /// </summary>
    Rejected = 0,

    /// <summary>
    /// 適用で飛ばした
    /// </summary>
    Skipped = 1,
}
