namespace Txfio;

/// <summary>
/// コピー進捗の 1 回分の報告
/// </summary>
public readonly record struct TransferProgress
{
    /// <summary>
    /// 書き終えたバイト数と、開始時点の残りバイト数を指定する
    /// </summary>
    /// <param name="bytesCopied">.txnew に書き終えたバイト数</param>
    /// <param name="totalBytes">開始時点の残りバイト数。不明なときは null</param>
    public TransferProgress(long bytesCopied, long? totalBytes)
    {
        BytesCopied = bytesCopied;
        TotalBytes = totalBytes;
    }

    /// <summary>
    /// .txnew に書き終えたバイト数
    /// </summary>
    public long BytesCopied { get; }

    /// <summary>
    /// 開始時点の残りバイト数。不明なときは null
    /// </summary>
    public long? TotalBytes { get; }
}
