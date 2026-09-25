namespace Txfio;

/// <summary>
/// 確定できなかった 1 操作
/// </summary>
public sealed class OperationReport
{
    /// <summary>
    /// パスと理由を指定する
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <param name="newPath">Move の移動先（それ以外は null）</param>
    /// <param name="kind">操作の種類</param>
    /// <param name="disposition">検証で拒んだか、適用で飛ばしたか</param>
    /// <param name="reason">失敗した理由</param>
    public OperationReport(
        string path,
        string? newPath,
        PendingChangeKind kind,
        OperationDisposition disposition,
        OperationFailureReason reason)
    {
        Path = path;
        NewPath = newPath;
        Kind = kind;
        Disposition = disposition;
        Reason = reason;
    }

    /// <summary>
    /// 対象パス
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Move の移動先パス
    /// </summary>
    public string? NewPath { get; }

    /// <summary>
    /// 操作の種類
    /// </summary>
    public PendingChangeKind Kind { get; }

    /// <summary>
    /// 検証で拒んだか、適用で飛ばしたか
    /// </summary>
    public OperationDisposition Disposition { get; }

    /// <summary>
    /// 失敗した理由
    /// </summary>
    public OperationFailureReason Reason { get; }

    /// <summary>
    /// ジャーナルの操作から、確定できなかった 1 件を作る
    /// </summary>
    /// <param name="operation">対象の操作</param>
    /// <param name="disposition">検証で拒んだか、適用で飛ばしたか</param>
    /// <param name="reason">失敗した理由</param>
    /// <returns>パスと理由を持った報告</returns>
    internal static OperationReport Create(
        JournalOperation operation,
        OperationDisposition disposition,
        OperationFailureReason reason)
    {
        string? newPath = operation.Kind == PendingChangeKind.Move ? operation.NewPath : null;
        return new OperationReport(operation.Path, newPath, operation.Kind, disposition, reason);
    }
}
