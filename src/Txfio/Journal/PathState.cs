using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// パスの存在と、ファイルならサイズ・最終更新日時
/// </summary>
internal sealed class PathState
{
    /// <summary>
    /// 存在の有無と、ファイルならサイズ・最終更新日時を指定する
    /// </summary>
    /// <param name="exists">パスがあるなら <see langword="true"/></param>
    /// <param name="length">ファイルのサイズ（ディレクトリと不在は null）</param>
    /// <param name="lastWriteTimeUtc">ファイルの最終更新日時（UTC、ディレクトリと不在は null）</param>
    [JsonConstructor]
    public PathState(bool exists, long? length = null, DateTime? lastWriteTimeUtc = null)
    {
        Exists = exists;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>
    /// パスがあるかどうか
    /// </summary>
    public bool Exists { get; }

    /// <summary>
    /// ファイルのサイズ（ディレクトリと不在は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Length { get; }

    /// <summary>
    /// ファイルの最終更新日時（UTC、ディレクトリと不在は null）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastWriteTimeUtc { get; }

    /// <summary>
    /// パスが無い状態
    /// </summary>
    internal static PathState Absent { get; } = new PathState(exists: false);

    /// <summary>
    /// ディレクトリとして存在するかどうか
    /// </summary>
    internal bool IsDirectory => Exists && Length is null && LastWriteTimeUtc is null;

    /// <summary>
    /// ファイルとして存在するかどうか
    /// </summary>
    internal bool IsFile => Exists && Length is not null && LastWriteTimeUtc is not null;

    /// <summary>
    /// ディスク上のパスをファイル・ディレクトリ・不在として読む
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <returns>そのパスの状態</returns>
    internal static PathState Capture(string path)
    {
        if (File.Exists(path))
        {
            FileInfo info = new FileInfo(path);
            return new PathState(exists: true, info.Length, info.LastWriteTimeUtc);
        }

        if (Directory.Exists(path))
        {
            return new PathState(exists: true);
        }

        return Absent;
    }

    /// <summary>
    /// ディスク上のパスがこの状態と完全一致するかを判定する
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <returns>一致すれば <see langword="true"/></returns>
    internal bool Matches(string path)
    {
        if (File.Exists(path))
        {
            if (Length is null || LastWriteTimeUtc is null)
            {
                return false;
            }

            FileInfo info = new FileInfo(path);
            return info.Length == Length.Value && info.LastWriteTimeUtc == LastWriteTimeUtc.Value;
        }

        if (Directory.Exists(path))
        {
            return IsDirectory;
        }

        return !Exists;
    }

    /// <summary>
    /// 別の状態と存在・サイズ・最終更新日時が同じかを判定する
    /// </summary>
    /// <param name="other">比較する状態</param>
    /// <returns>同じなら <see langword="true"/></returns>
    internal bool SameAs(PathState other)
    {
        return Exists == other.Exists
            && Length == other.Length
            && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }
}
