namespace Txfio;

/// <summary>
/// 持ち主のいない残骸ジャーナルがあるあいだ、新しいトランザクションを拒否する
/// </summary>
internal static class StaleJournals
{
    /// <summary>
    /// 残骸ジャーナルがあれば例外を投げる
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <exception cref="RecoveryRequiredException">残骸ジャーナルがある</exception>
    internal static void ThrowIfAny(string workFolder)
    {
        if (Exists(workFolder))
        {
            throw new RecoveryRequiredException(
                "復旧していないトランザクションがあります。先に RecoverAsync を呼んでください: " + workFolder,
                workFolder);
        }
    }

    /// <summary>
    /// 生存ロックを開けて、開けたあともジャーナルが残っているものがあるかを返す。ジャーナルは読まない
    /// </summary>
    /// <param name="workFolder">ワークフォルダ</param>
    /// <returns>残骸ジャーナルがあれば <see langword="true"/></returns>
    internal static bool Exists(string workFolder)
    {
        string metadataFolder = MetadataNames.FolderPath(workFolder);
        if (!Directory.Exists(metadataFolder))
        {
            return false;
        }

        foreach (string journalPath in Directory.GetFiles(
            metadataFolder,
            MetadataNames.JournalSearchPattern,
            SearchOption.TopDirectoryOnly))
        {
            // 共有違反なら持ち主が生きている。自分のジャーナルも自分が持つので必ずここで飛ぶ
            using FileStream? liveness = LivenessLock.TryOpenStale(MetadataNames.LivenessLockPath(journalPath));
            if (liveness is not null && File.Exists(journalPath))
            {
                return true;
            }
        }

        return false;
    }
}
