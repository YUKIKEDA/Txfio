using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// ジャーナルの末尾に追記する 1 行（表の末尾に足した操作と作成ディレクトリ）
/// </summary>
internal sealed class JournalAppend
{
    /// <summary>
    /// 足した操作と作成ディレクトリを指定する
    /// </summary>
    /// <param name="append">表の末尾に足した操作（無ければ null）</param>
    /// <param name="createdDirectories">末尾に足した作成ディレクトリ（無ければ null）</param>
    [JsonConstructor]
    public JournalAppend(
        IReadOnlyList<JournalOperation>? append = null,
        IReadOnlyList<string>? createdDirectories = null)
    {
        Append = append ?? Array.Empty<JournalOperation>();
        CreatedDirectories = createdDirectories ?? Array.Empty<string>();
    }

    /// <summary>
    /// 表の末尾に足した操作
    /// </summary>
    public IReadOnlyList<JournalOperation> Append { get; }

    /// <summary>
    /// 末尾に足した作成ディレクトリ
    /// </summary>
    public IReadOnlyList<string> CreatedDirectories { get; }
}
