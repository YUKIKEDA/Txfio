namespace Txfio;

/// <summary>
/// コミット後の姿で、ディレクトリの直下にある 1 件
/// </summary>
public sealed class DirectoryEntry
{
    /// <summary>
    /// パスと種類を指定する
    /// </summary>
    /// <param name="path">絶対パス</param>
    /// <param name="isDirectory">ディレクトリなら <see langword="true"/>、ファイルなら <see langword="false"/></param>
    public DirectoryEntry(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
    }

    /// <summary>
    /// 絶対パス
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// ディレクトリなら <see langword="true"/>、ファイルなら <see langword="false"/>
    /// </summary>
    public bool IsDirectory { get; }
}
