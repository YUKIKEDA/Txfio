namespace Txfio;

/// <summary>
/// メタデータフォルダ、ジャーナル、ロックの名前
/// </summary>
internal static class MetadataNames
{
    /// <summary>
    /// ワークフォルダ直下のメタデータフォルダ名
    /// </summary>
    internal const string FolderName = ".txfio";

    /// <summary>
    /// ジャーナルファイルを列挙するときの検索パターン
    /// </summary>
    internal const string JournalSearchPattern = "tx-*.journal";

    /// <summary>
    /// ロックファイルを置くフォルダ名
    /// </summary>
    internal const string LockFolderName = "locks";

    /// <summary>
    /// メタデータフォルダの絶対パスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>`.txfio` フォルダのパス</returns>
    internal static string FolderPath(string workFolder)
    {
        return System.IO.Path.Combine(workFolder, FolderName);
    }

    /// <summary>
    /// ロックファイルを置くフォルダの絶対パスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>`.txfio/locks` フォルダのパス</returns>
    internal static string LockFolderPath(string workFolder)
    {
        return System.IO.Path.Combine(FolderPath(workFolder), LockFolderName);
    }

    /// <summary>
    /// ワークフォルダ全体のロックを持たないあいだに開くしるしのパスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>しるし（`.txfio/share-lost.lock`）のパス</returns>
    internal static string ShareLostLockPath(string workFolder)
    {
        return System.IO.Path.Combine(FolderPath(workFolder), "share-lost.lock");
    }

    /// <summary>
    /// トランザクションに対応するジャーナルファイルのパスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <param name="transactionId">トランザクション ID</param>
    /// <returns>ジャーナルファイルのパス</returns>
    internal static string JournalPath(string workFolder, Guid transactionId)
    {
        return System.IO.Path.Combine(
            FolderPath(workFolder),
            "tx-" + transactionId.ToString("D") + ".journal");
    }

    /// <summary>
    /// ジャーナルのパスから、それを持つワークフォルダを返す
    /// </summary>
    /// <param name="journalPath">`.txfio/tx-{guid}.journal` のパス</param>
    /// <returns>`.txfio` の親のワークフォルダ</returns>
    internal static string WorkFolderFromJournal(string journalPath)
    {
        string metadataFolder = System.IO.Path.GetDirectoryName(journalPath)!;
        return System.IO.Path.GetDirectoryName(metadataFolder)!;
    }

    /// <summary>
    /// ジャーナルと組になる生存ロックのパスを返す
    /// </summary>
    /// <param name="journalPath">`.txfio/tx-{guid}.journal` のパス</param>
    /// <returns>`.txfio/tx-{guid}.lock` のパス</returns>
    internal static string LivenessLockPath(string journalPath)
    {
        return System.IO.Path.ChangeExtension(journalPath, ".lock");
    }

    /// <summary>
    /// 上書き用の一時ファイルのパスを返す
    /// </summary>
    /// <param name="journalPath">`.txfio/tx-{guid}.journal` のパス</param>
    /// <returns>`.txfio/tx-{guid}.journal.tmp` のパス</returns>
    internal static string JournalTempPath(string journalPath)
    {
        return journalPath + ".tmp";
    }

    /// <summary>
    /// ジャーナルのファイル名からトランザクション ID を取る
    /// </summary>
    /// <param name="journalPath">`.txfio/tx-{guid}.journal` のパス</param>
    /// <param name="transactionId">取れた ID</param>
    /// <returns>ファイル名が `tx-{guid}.journal` なら <see langword="true"/></returns>
    internal static bool TryGetTransactionId(string journalPath, out Guid transactionId)
    {
        string name = System.IO.Path.GetFileName(journalPath);
        const string prefix = "tx-";
        const string suffix = ".journal";
        if (name.Length > prefix.Length + suffix.Length
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string id = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            return Guid.TryParseExact(id, "D", out transactionId);
        }

        transactionId = Guid.Empty;
        return false;
    }
}
