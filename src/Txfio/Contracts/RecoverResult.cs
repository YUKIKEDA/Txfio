namespace Txfio;

/// <summary>
/// <see cref="RecoverReport.Result"/> の値
/// </summary>
public enum RecoverResult
{
    /// <summary>
    /// 未確定のトランザクションは無かった
    /// </summary>
    NoPendingTransactions = 0,

    /// <summary>
    /// 未コミットのジャーナルを破棄してロールバックした
    /// </summary>
    RolledBack = 1,

    /// <summary>
    /// Committing 中の操作をロールフォワードした
    /// </summary>
    RolledForward = 2,

    /// <summary>
    /// Before / After のどちらとも一致しない操作があった
    /// </summary>
    ConflictDetected = 3,

    /// <summary>
    /// JSON として読めないジャーナルがあった
    /// </summary>
    JournalUnreadable = 4,
}
