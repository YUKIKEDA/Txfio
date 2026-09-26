namespace Txfio;

/// <summary>
/// 問い合わせたパスのコミット後の姿
/// </summary>
internal readonly struct CommitAppearance
{
    private CommitAppearance(bool exists, bool isDirectory, string? contentPath, string? stagedFor)
    {
        Exists = exists;
        IsDirectory = isDirectory;
        ContentPath = contentPath;
        StagedFor = stagedFor;
    }

    /// <summary>
    /// ファイルかディレクトリがあるなら <see langword="true"/>
    /// </summary>
    internal bool Exists { get; }

    /// <summary>
    /// ディレクトリなら <see langword="true"/>
    /// </summary>
    internal bool IsDirectory { get; }

    /// <summary>
    /// 読む実体のパス（無いときは null）
    /// </summary>
    internal string? ContentPath { get; }

    /// <summary>
    /// 中身がステージングファイルのとき、それを書いた操作の対象パス（実ファイルを読むときは null）
    /// </summary>
    internal string? StagedFor { get; }

    /// <summary>
    /// 無い姿を返す
    /// </summary>
    /// <returns>無い姿</returns>
    internal static CommitAppearance Absent()
    {
        return new CommitAppearance(exists: false, isDirectory: false, contentPath: null, stagedFor: null);
    }

    /// <summary>
    /// 実ファイルをそのまま読むファイルの姿を返す
    /// </summary>
    /// <param name="contentPath">読む実ファイルのパス</param>
    /// <returns>ファイルがある姿</returns>
    internal static CommitAppearance File(string contentPath)
    {
        return new CommitAppearance(exists: true, isDirectory: false, contentPath, stagedFor: null);
    }

    /// <summary>
    /// ステージングファイルを読むファイルの姿を返す
    /// </summary>
    /// <param name="operation">中身を決めた Add か Update</param>
    /// <returns>ファイルがある姿</returns>
    internal static CommitAppearance Staged(JournalOperation operation)
    {
        return new CommitAppearance(exists: true, isDirectory: false, operation.StagingPath, operation.Path);
    }

    /// <summary>
    /// ディレクトリの姿を返す
    /// </summary>
    /// <param name="contentPath">実体のパス</param>
    /// <returns>ディレクトリがある姿</returns>
    internal static CommitAppearance Directory(string contentPath)
    {
        return new CommitAppearance(exists: true, isDirectory: true, contentPath, stagedFor: null);
    }
}
