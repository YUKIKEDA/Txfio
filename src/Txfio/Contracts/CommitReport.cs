namespace Txfio;

/// <summary>
/// <see cref="ITransaction.CommitAsync"/> の結果
/// </summary>
public sealed class CommitReport
{
    /// <summary>
    /// 全体の結果と、確定できなかった操作を指定する
    /// </summary>
    /// <param name="result">全体の結果</param>
    /// <param name="operations">拒んだ操作、または飛ばした操作（成功のときは空）</param>
    public CommitReport(CommitResult result, IReadOnlyList<OperationReport> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Result = result;
        Operations = operations;
    }

    /// <summary>
    /// 全体の結果
    /// </summary>
    public CommitResult Result { get; }

    /// <summary>
    /// 拒んだ操作、または飛ばした操作（成功のときは空）
    /// </summary>
    public IReadOnlyList<OperationReport> Operations { get; }
}
