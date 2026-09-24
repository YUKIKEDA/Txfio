namespace Txfio;

/// <summary>
/// トランザクションが生きているあいだ持ち続ける `.txfio/tx-{guid}.lock`
/// </summary>
internal static class LivenessLock
{
    /// <summary>
    /// 開始するトランザクションの生存ロックを作って開く
    /// </summary>
    /// <param name="lockPath">生存ロックのパス</param>
    /// <returns>トランザクションが終わるまで持つハンドル</returns>
    internal static FileStream Create(string lockPath)
    {
        return Open(lockPath, FileMode.CreateNew);
    }

    /// <summary>
    /// 持ち主のいないジャーナルの生存ロックを開く。ファイルが無ければ作る
    /// </summary>
    /// <param name="lockPath">生存ロックのパス</param>
    /// <returns>開けたハンドル。持ち主が生きていれば <see langword="null"/></returns>
    internal static FileStream? TryOpenStale(string lockPath)
    {
        try
        {
            return Open(lockPath, FileMode.OpenOrCreate);
        }
        catch (IOException exception) when (PathLockSet.IsSharingViolation(exception))
        {
            return null;
        }
    }

    private static FileStream Open(string lockPath, FileMode mode)
    {
        return new FileStream(
            lockPath,
            mode,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }
}
