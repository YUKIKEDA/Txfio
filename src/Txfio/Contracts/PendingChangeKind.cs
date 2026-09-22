namespace Txfio;

/// <summary>
/// ジャーナルに記録する操作の種類
/// </summary>
public enum PendingChangeKind
{
    /// <summary>
    /// 新規ファイルの追加
    /// </summary>
    Add = 0,

    /// <summary>
    /// 既存ファイルの更新
    /// </summary>
    Update = 1,

    /// <summary>
    /// 削除予約
    /// </summary>
    Delete = 2,

    /// <summary>
    /// 同一ボリューム内の移動またはリネーム
    /// </summary>
    Move = 3,

    /// <summary>
    /// 外部が作った既存ファイルの取り込み
    /// </summary>
    Attach = 4,
}
