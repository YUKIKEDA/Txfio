namespace Txfio.Tests.Stress;

/// <summary>
/// ディレクトリのランダム列で使う操作の種類
/// </summary>
internal enum DirectoryOperationKind
{
    /// <summary>
    /// 無いパスへの CreateDirectoryAsync
    /// </summary>
    CreateDirectory,

    /// <summary>
    /// 無いパスへの AddAsync
    /// </summary>
    Add,

    /// <summary>
    /// あるファイルへの UpdateAsync
    /// </summary>
    Update,

    /// <summary>
    /// ファイル、または空ディレクトリの DeleteAsync
    /// </summary>
    Delete,

    /// <summary>
    /// ディレクトリの DeleteTreeAsync
    /// </summary>
    DeleteTree,

    /// <summary>
    /// 上書きしない MoveAsync
    /// </summary>
    Move,

    /// <summary>
    /// あるファイルの ReadAsync
    /// </summary>
    Read,
}
