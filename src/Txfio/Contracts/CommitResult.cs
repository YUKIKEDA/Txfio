namespace Txfio;

/// <summary>
/// <see cref="ITransaction.CommitAsync"/> の結果
/// </summary>
public enum CommitResult
{
    /// <summary>
    /// 全操作が想定どおり適用された
    /// </summary>
    Succeeded = 0,

    /// <summary>
    /// 一部操作で外部干渉があったが確定はした
    /// </summary>
    PartialConflict = 1,

    /// <summary>
    /// コミット前検証で失敗し、実体には触れていない
    /// </summary>
    Failed = 2,
}
