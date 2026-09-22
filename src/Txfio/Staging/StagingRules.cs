namespace Txfio;

/// <summary>
/// Add / Update / Delete / Move / Attach の前提チェックと同一パスの正規化
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

        if (existingKind == PendingChangeKind.Attach && requestedKind == PendingChangeKind.Update)
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
    /// 移動元が既存ファイルであることを検証する
    /// </summary>
    /// <param name="sourcePath">移動元パス</param>
    internal static void EnsureMoveSourceExists(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            throw new IOException("ディレクトリの移動は未対応です: " + sourcePath);
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("移動元のファイルが存在しません: " + sourcePath, sourcePath);
        }
    }

    /// <summary>
    /// 移動先にファイルもディレクトリも無いことを検証する
    /// </summary>
    /// <param name="destPath">移動先パス</param>
    internal static void EnsureMoveDestinationIsFree(string destPath)
    {
        if (Directory.Exists(destPath))
        {
            throw new IOException("移動先がディレクトリです: " + destPath);
        }

        if (File.Exists(destPath))
        {
            throw new IOException("移動先のファイルが既に存在します: " + destPath);
        }
    }

    /// <summary>
    /// 移動元と移動先が同一ボリュームかを検証する
    /// </summary>
    /// <param name="sourcePath">移動元パス</param>
    /// <param name="destPath">移動先パス</param>
    internal static void EnsureSameVolume(string sourcePath, string destPath)
    {
        string? sourceRoot = System.IO.Path.GetPathRoot(sourcePath);
        string? destRoot = System.IO.Path.GetPathRoot(destPath);
        if (string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(destRoot)
            || !string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("ボリュームをまたぐ移動はできません: " + sourcePath + " -> " + destPath);
        }
    }

    /// <summary>
    /// 取り込み対象が既存ファイルであることを検証する
    /// </summary>
    /// <param name="targetPath">対象パス</param>
    internal static void EnsureAttachTarget(string targetPath)
    {
        if (Directory.Exists(targetPath))
        {
            throw new IOException("ディレクトリの取り込みは未対応です: " + targetPath);
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("取り込み対象のファイルが存在しません: " + targetPath, targetPath);
        }
    }

    /// <summary>
    /// 対象ファイルのサイズと最終更新日時が期待どおりかを判定する
    /// </summary>
    /// <param name="path">対象パス</param>
    /// <param name="expectedLength">期待するサイズ</param>
    /// <param name="expectedLastWriteTimeUtc">期待する最終更新日時（UTC）</param>
    /// <returns>ファイルがあり、サイズと最終更新日時が一致すれば <see langword="true"/></returns>
    internal static bool MatchesExpectedState(string path, long? expectedLength, DateTime? expectedLastWriteTimeUtc)
    {
        if (!expectedLength.HasValue || !expectedLastWriteTimeUtc.HasValue || !File.Exists(path))
        {
            return false;
        }

        FileInfo info = new FileInfo(path);
        return info.Length == expectedLength.Value
            && info.LastWriteTimeUtc == expectedLastWriteTimeUtc.Value;
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
