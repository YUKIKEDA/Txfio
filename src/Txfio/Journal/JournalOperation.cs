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
    /// <param name="stagingPath">ステージングファイル（`.txnew`）のパス（Add / Update 以外は null）</param>
    /// <param name="newPath">Move の移動先（それ以外は null）</param>
    /// <param name="before">対象パスの適用直前状態（未記録なら null）</param>
    /// <param name="after">対象パスの適用直後状態（未記録なら null）</param>
    /// <param name="destBefore">Move の移動先の適用直前状態（それ以外は null）</param>
    /// <param name="destAfter">Move の移動先の適用直後状態（それ以外は null）</param>
    /// <param name="isDirectory">ディレクトリの Delete、DeleteTree、Move、または CreateDirectory なら <see langword="true"/></param>
    /// <param name="directoryCreated">CreateDirectory がディレクトリを作り終えたなら <see langword="true"/></param>
    /// <param name="overwrite">置き換えの Move なら <see langword="true"/></param>
    [JsonConstructor]
    public JournalOperation(
        PendingChangeKind kind,
        string path,
        string? stagingPath = null,
        string? newPath = null,
        PathState? before = null,
        PathState? after = null,
        PathState? destBefore = null,
        PathState? destAfter = null,
        bool isDirectory = false,
        bool directoryCreated = false,
        bool overwrite = false)
    {
        Kind = kind;
        Path = path;
        StagingPath = stagingPath;
        NewPath = newPath;
        Before = before;
        After = after;
        DestBefore = destBefore;
        DestAfter = destAfter;
        IsDirectory = isDirectory;
        DirectoryCreated = directoryCreated;
        Overwrite = overwrite;
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
    /// ステージングファイル（`.txnew`）のパス（Add / Update 以外は null）
    /// </summary>
    public string? StagingPath { get; }

    /// <summary>
    /// Move の移動先パス
    /// </summary>
    public string? NewPath { get; }

    /// <summary>
    /// 対象パスの適用直前状態（未記録なら null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? Before { get; }

    /// <summary>
    /// 対象パスの適用直後状態（未記録なら null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? After { get; }

    /// <summary>
    /// Move の移動先の適用直前状態（それ以外は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? DestBefore { get; }

    /// <summary>
    /// Move の移動先の適用直後状態（それ以外は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? DestAfter { get; }

    /// <summary>
    /// ディレクトリの Delete、DeleteTree、Move、または CreateDirectory なら <see langword="true"/>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDirectory { get; }

    /// <summary>
    /// CreateDirectory がディレクトリを作り終えたなら <see langword="true"/>（未作成のまま落ちたときは、同じ名前のディレクトリを他が作ったかもしれない）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DirectoryCreated { get; }

    /// <summary>
    /// 置き換えの Move なら <see langword="true"/>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Overwrite { get; }

    /// <summary>
    /// Before / After を付けたコピーを返す
    /// </summary>
    /// <param name="before">対象パスの適用直前状態</param>
    /// <param name="after">対象パスの適用直後状態</param>
    /// <param name="destBefore">Move の移動先の適用直前状態</param>
    /// <param name="destAfter">Move の移動先の適用直後状態</param>
    /// <returns>状態を記録した操作</returns>
    internal JournalOperation WithOutcome(
        PathState before,
        PathState after,
        PathState? destBefore = null,
        PathState? destAfter = null)
    {
        return new JournalOperation(
            Kind,
            Path,
            StagingPath,
            NewPath,
            before,
            after,
            destBefore,
            destAfter,
            IsDirectory,
            DirectoryCreated,
            Overwrite);
    }

    /// <summary>
    /// 作成済みを付けたコピーを返す
    /// </summary>
    /// <returns>作成済みを記録した操作</returns>
    internal JournalOperation WithDirectoryCreated()
    {
        return new JournalOperation(
            Kind,
            Path,
            StagingPath,
            NewPath,
            Before,
            After,
            DestBefore,
            DestAfter,
            IsDirectory,
            directoryCreated: true,
            overwrite: Overwrite);
    }
}
