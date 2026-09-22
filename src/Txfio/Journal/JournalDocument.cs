using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// ジャーナルファイルへ書き出す JSON の構造
/// </summary>
internal sealed class JournalDocument
{
    /// <summary>
    /// ジャーナル文書を作成する
    /// </summary>
    /// <param name="version">文書形式の版</param>
    /// <param name="transactionId">対象トランザクションの ID</param>
    /// <param name="committing">コミットの適用中なら <see langword="true"/></param>
    /// <param name="operations">未確定の操作一覧</param>
    [JsonConstructor]
    public JournalDocument(
        int version,
        Guid transactionId,
        bool committing,
        IReadOnlyList<JournalOperation>? operations = null)
    {
        Version = version;
        TransactionId = transactionId;
        Committing = committing;
        Operations = operations ?? Array.Empty<JournalOperation>();
    }

    /// <summary>
    /// 文書形式の版
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// 対象トランザクションの ID
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// コミットの適用中かどうか
    /// </summary>
    public bool Committing { get; }

    /// <summary>
    /// 未確定の操作一覧
    /// </summary>
    public IReadOnlyList<JournalOperation> Operations { get; }
}
