namespace Txfio;

/// <content>
/// 文字列と JSON の書き込み時に、コミット後の姿にファイルがあるかを見る
/// </content>
internal sealed partial class Transaction
{
    /// <summary>
    /// コミット後の姿に対象のファイルがあるかを返す
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <returns>ファイルがあれば true、ディレクトリは false</returns>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    internal bool FileExistsInCommitView(string path)
    {
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        CommitAppearance appearance = CommitView.Resolve(_operations, targetPath);
        return appearance.Exists && !appearance.IsDirectory;
    }
}
