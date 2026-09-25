namespace Txfio;

/// <summary>
/// ワークフォルダ内のパス正規化
/// </summary>
internal static class WorkPath
{
    /// <summary>
    /// パスをワークフォルダ基準の絶対パスに正規化する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="path">相対または絶対の対象パス</param>
    /// <returns>正規化した絶対パス</returns>
    /// <exception cref="ArgumentException">ワークフォルダの外側を指している</exception>
    /// <exception cref="InvalidOperationException">対象自身、またはワークフォルダ自身を除く祖先がリパースポイントである</exception>
    internal static string ResolveInWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string combined = System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(workFolder, path);
        string fullPath = System.IO.Path.GetFullPath(combined);
        if (!IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("パスはワークフォルダの内側である必要があります", nameof(path));
        }

        ThrowIfReparseInside(workFolder, fullPath);
        return fullPath;
    }

    /// <summary>
    /// パスをワークフォルダの外の絶対パスに正規化する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="path">絶対パス、または現在ディレクトリ基準の相対パス</param>
    /// <returns>正規化した絶対パス</returns>
    /// <exception cref="ArgumentException">ワークフォルダの内側を指している</exception>
    internal static string ResolveOutsideWorkFolder(string workFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        if (IsInsideWorkFolder(workFolder, fullPath))
        {
            throw new ArgumentException("パスはワークフォルダの外側である必要があります", nameof(path));
        }

        return fullPath;
    }

    /// <summary>
    /// メタデータフォルダそのもの、またはその配下かどうかを判定する
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="fullPath">正規化した絶対パス</param>
    /// <returns>メタデータフォルダそのもの、またはその配下なら <see langword="true"/></returns>
    internal static bool IsInMetadataFolder(string workFolder, string fullPath)
    {
        return IsEqualOrUnder(MetadataNames.FolderPath(workFolder), fullPath);
    }

    /// <summary>
    /// 対象ファイルと同じディレクトリの `.txnew` パスを返す
    /// </summary>
    /// <param name="targetPath">対象ファイルの絶対パス</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <returns>ステージングファイルのパス</returns>
    internal static string StagingFilePath(string targetPath, Guid transactionId)
    {
        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("対象パスの親ディレクトリを特定できません", nameof(targetPath));
        }

        string fileName = System.IO.Path.GetFileName(targetPath);
        return System.IO.Path.Combine(
            directory,
            fileName + "." + transactionId.ToString("D") + ".txnew");
    }

    /// <summary>
    /// このトランザクションの `.txnew` かどうかを判定する
    /// </summary>
    /// <param name="path">調べるパス</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <returns>このトランザクションの `.txnew` なら <see langword="true"/></returns>
    internal static bool IsThisTransactionStagingFile(string path, Guid transactionId)
    {
        return path.EndsWith(
            "." + transactionId.ToString("D") + ".txnew",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfReparseInside(string workFolder, string fullPath)
    {
        string root = System.IO.Path.TrimEndingDirectorySeparator(workFolder);
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(current);
            if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsReparsePoint(trimmed))
            {
                throw new InvalidOperationException("リパースポイントは操作できません: " + trimmed);
            }

            current = System.IO.Path.GetDirectoryName(trimmed);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsInsideWorkFolder(string workFolder, string fullPath)
    {
        return !string.Equals(workFolder, fullPath, StringComparison.OrdinalIgnoreCase)
            && IsEqualOrUnder(workFolder, fullPath);
    }

    private static bool IsEqualOrUnder(string parent, string fullPath)
    {
        if (string.Equals(parent, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = parent.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
