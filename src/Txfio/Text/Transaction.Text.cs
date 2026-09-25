namespace Txfio;

/// <content>
/// 文字列と JSON の書き込みで、ディスク上のファイルの有無と Move の移動先を見る
/// </content>
internal sealed partial class Transaction
{
    /// <summary>
    /// ディスク上に対象のファイルがあるか
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <returns>ファイルがあれば true。未コミットのサイドカーは見ない</returns>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    internal bool FileExistsOnDisk(string path)
    {
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        return File.Exists(targetPath);
    }

    /// <summary>
    /// このトランザクションの Move の移動先か
    /// </summary>
    /// <param name="path">対象パス（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
    /// <returns>移動先なら true</returns>
    /// <exception cref="ArgumentException">パスがワークフォルダの外である</exception>
    internal bool IsMoveDestination(string path)
    {
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        return FindMoveToIndex(targetPath) >= 0;
    }
}
