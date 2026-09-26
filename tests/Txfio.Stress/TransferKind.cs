namespace Txfio.Tests.Stress;

/// <summary>
/// ワークフォルダの内外をまたぐランダム列の操作種別
/// </summary>
internal enum TransferKind
{
    /// <summary>
    /// ワークフォルダの中から中へコピーする
    /// </summary>
    Copy,

    /// <summary>
    /// ワークフォルダの外から中へ取り込む
    /// </summary>
    Import,

    /// <summary>
    /// ワークフォルダの中から外へ書き出す
    /// </summary>
    Export,
}
