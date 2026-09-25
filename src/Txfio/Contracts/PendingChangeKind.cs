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
    /// ディレクトリとその配下すべての削除予約
    /// </summary>
    DeleteTree = 4,

    /// <summary>
    /// 空ディレクトリを呼び出した時点で作る（配下の操作は別のエントリになる）
    /// </summary>
    CreateDirectory = 5,
}
