namespace Txfio;

/// <summary>
/// 他のトランザクションがパスを押さえている
/// </summary>
public sealed class LockContentionException : TxfioException
{
    /// <summary>
    /// メッセージと失敗したパスを指定して例外を作る
    /// </summary>
    /// <param name="message">例外メッセージ</param>
    /// <param name="path">失敗したパス</param>
    public LockContentionException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// 失敗したパス
    /// </summary>
    public string Path { get; }
}
