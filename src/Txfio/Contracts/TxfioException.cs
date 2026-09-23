namespace Txfio;

/// <summary>
/// Txfio が呼び出し側へ返す例外の基底
/// </summary>
public abstract class TxfioException : Exception
{
    /// <summary>
    /// メッセージを指定して例外を作る
    /// </summary>
    /// <param name="message">例外メッセージ</param>
    protected TxfioException(string message)
        : base(message)
    {
    }
}
