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
    /// ジャーナルと組になる生存ロックのパスを返す
    /// </summary>
    /// <param name="journalPath">`.txfio/tx-{guid}.journal` のパス</param>
    /// <returns>`.txfio/tx-{guid}.lock` のパス</returns>
    internal static string LivenessLockPath(string journalPath)
    {
        return System.IO.Path.ChangeExtension(journalPath, ".lock");
    }
}
