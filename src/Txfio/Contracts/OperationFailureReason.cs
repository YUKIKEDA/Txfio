namespace Txfio;

/// <summary>
/// 操作を確定できなかった理由
/// </summary>
public enum OperationFailureReason
{
    /// <summary>
    /// 対象が無い
    /// </summary>
    Missing = 0,

    /// <summary>
    /// 既にある
    /// </summary>
    AlreadyExists = 1,

    /// <summary>
    /// ファイルかディレクトリにすり替わった
    /// </summary>
    ReplacedByFile = 2,

    /// <summary>
    /// ディレクトリの直下条件を満たさない
    /// </summary>
    DirectoryPreconditions = 3,

    /// <summary>
    /// Before と After のどちらとも一致しない
    /// </summary>
    BeforeAfterMismatch = 4,

    /// <summary>
    /// 共有違反
    /// </summary>
    SharingViolation = 5,

    /// <summary>
    /// それ以外の IO 失敗
    /// </summary>
    IoFailure = 6,
}
