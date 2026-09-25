namespace Txfio;

/// <summary>
/// パスの表からコミット後の姿を解く（ワークフォルダ全体は走査しない）
/// </summary>
internal static class CommitView
{
    /// <summary>
    /// 問い合わせたパスのコミット後の姿を返す
    /// </summary>
    /// <param name="operations">現在の操作一覧</param>
    /// <param name="targetPath">正規化済みの絶対パス</param>
    /// <returns>コミット後の姿</returns>
    internal static CommitAppearance Resolve(IReadOnlyList<JournalOperation> operations, string targetPath)
    {
        return PathTable.Resolve(operations, targetPath);
    }
}
