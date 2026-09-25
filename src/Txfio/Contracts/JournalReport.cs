namespace Txfio;

/// <summary>
/// 復旧した 1 ジャーナル
/// </summary>
public sealed class JournalReport
{
    /// <summary>
    /// トランザクションと、そのジャーナルの結果を指定する
    /// </summary>
    /// <param name="transactionId">ジャーナルのトランザクション ID</param>
    /// <param name="result">そのジャーナルの結果</param>
    /// <param name="operations">競合して飛ばした操作（競合が無いときと、読めないときは空）</param>
    public JournalReport(Guid transactionId, RecoverResult result, IReadOnlyList<OperationReport> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        TransactionId = transactionId;
        Result = result;
        Operations = operations;
    }

    /// <summary>
    /// ジャーナルのトランザクション ID
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// そのジャーナルの結果
    /// </summary>
    public RecoverResult Result { get; }

    /// <summary>
    /// 競合して飛ばした操作（競合が無いときと、読めないときは空）
    /// </summary>
    public IReadOnlyList<OperationReport> Operations { get; }
}
