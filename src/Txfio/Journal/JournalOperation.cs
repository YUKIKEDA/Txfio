using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// ジャーナルに記録する 1 操作
/// </summary>
internal sealed class JournalOperation
{
    /// <summary>
    /// 操作の種類とパスを指定する
    /// </summary>
    /// <param name="kind">操作の種類</param>
    /// <param name="path">対象パス</param>
    /// <param name="stagingPath">ステージングファイル（`.txnew`）のパス（Delete / Move / Attach は null）</param>
    /// <param name="newPath">Move の移動先（それ以外は null）</param>
    /// <param name="expectedLength">Attach 時点のサイズ（それ以外は null）</param>
    /// <param name="expectedLastWriteTimeUtc">Attach 時点の最終更新日時（UTC、それ以外は null）</param>
    [JsonConstructor]
    public JournalOperation(
        PendingChangeKind kind,
        string path,
        string? stagingPath = null,
        string? newPath = null,
        long? expectedLength = null,
        DateTime? expectedLastWriteTimeUtc = null)
    {
        Kind = kind;
        Path = path;
        StagingPath = stagingPath;
        NewPath = newPath;
        ExpectedLength = expectedLength;
        ExpectedLastWriteTimeUtc = expectedLastWriteTimeUtc;
    }

    /// <summary>
    /// 操作の種類
    /// </summary>
    public PendingChangeKind Kind { get; }

    /// <summary>
    /// 対象パス
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// ステージングファイル（`.txnew`）のパス（Delete / Move / Attach は null）
    /// </summary>
    public string? StagingPath { get; }

    /// <summary>
    /// Move の移動先パス
    /// </summary>
    public string? NewPath { get; }

    /// <summary>
    /// Attach 時点のサイズ（それ以外は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpectedLength { get; }

    /// <summary>
    /// Attach 時点の最終更新日時（UTC、それ以外は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpectedLastWriteTimeUtc { get; }
}
