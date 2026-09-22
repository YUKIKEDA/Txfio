namespace Txfio;

/// <summary>
/// メタデータフォルダとジャーナルの名前
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
    /// メタデータフォルダの絶対パスを返す
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>`.txfio` フォルダのパス</returns>
    internal static string FolderPath(string workFolder)
    {
        return System.IO.Path.Combine(workFolder, FolderName);
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
}
