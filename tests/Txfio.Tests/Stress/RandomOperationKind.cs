namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列で使う操作の種類
/// </summary>
internal enum RandomOperationKind
{
    /// <summary>
    /// 無いパスへの AddAsync
    /// </summary>
    Add,

    /// <summary>
    /// あるパスへの UpdateAsync
    /// </summary>
    Update,

    /// <summary>
    /// あるファイルの DeleteAsync
    /// </summary>
    Delete,

    /// <summary>
    /// あるファイルから無いパスへの MoveAsync
    /// </summary>
    Move,

    /// <summary>
    /// あるファイルの ReadAsync
    /// </summary>
    Read,
}
