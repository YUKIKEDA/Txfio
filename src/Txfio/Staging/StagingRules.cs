namespace Txfio;

/// <summary>
/// Add / Update / Delete の前提チェックと同一パスの正規化
/// </summary>
internal static class StagingRules
{
    /// <summary>
    /// 同一パスへ再ステージするときの記録種別を決める
    /// </summary>
    /// <param name="existingKind">既に記録されている種類</param>
    /// <param name="requestedKind">今回の操作の種類</param>
    /// <returns>ジャーナルに残す種類</returns>
    internal static PendingChangeKind NormalizeRestageKind(PendingChangeKind existingKind, PendingChangeKind requestedKind)
    {
        if (existingKind == requestedKind)
        {
            return existingKind;
        }

        if (existingKind == PendingChangeKind.Add && requestedKind == PendingChangeKind.Update)
        {
            return PendingChangeKind.Add;
        }

        if (existingKind == PendingChangeKind.Delete
            && (requestedKind == PendingChangeKind.Add || requestedKind == PendingChangeKind.Update))
        {
            return PendingChangeKind.Update;
        }

        throw new InvalidOperationException("このパスは既に別の操作でステージングされています");
    }

    /// <summary>
    /// Add / Update / Delete の対象ファイルの存在有無を検証する
    /// </summary>
    /// <param name="kind">操作の種類</param>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureTargetMatchesKind(PendingChangeKind kind, string targetPath)
    {
        if (kind == PendingChangeKind.Delete)
        {
            if (Directory.Exists(targetPath))
            {
                throw new IOException("ディレクトリの削除は未対応です: " + targetPath);
            }

            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException("削除対象のファイルが存在しません: " + targetPath, targetPath);
            }

            return;
        }

        bool exists = File.Exists(targetPath);
        if (kind == PendingChangeKind.Add && exists)
        {
            throw new IOException("追加対象のファイルが既に存在します: " + targetPath);
        }

        if (kind == PendingChangeKind.Update && !exists)
        {
            throw new FileNotFoundException("更新対象のファイルが存在しません: " + targetPath, targetPath);
        }
    }

    /// <summary>
    /// 対象の親ディレクトリが存在するかを検証する
    /// </summary>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureParentDirectoryExists(string targetPath)
    {
        string? parent = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("親ディレクトリが存在しません: " + parent);
        }
    }
}
