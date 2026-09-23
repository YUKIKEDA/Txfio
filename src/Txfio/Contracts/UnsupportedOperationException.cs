namespace Txfio;

/// <summary>
/// ライブラリが未対応の操作
/// </summary>
public sealed class UnsupportedOperationException : TxfioException
{
    /// <summary>
    /// メッセージを指定して例外を作る
    /// </summary>
    /// <param name="message">例外メッセージ</param>
    public UnsupportedOperationException(string message)
        : base(message)
    {
    }
}
