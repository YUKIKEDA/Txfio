namespace Txfio;

/// <summary>
/// ディスク上の前提が崩れている
/// </summary>
public sealed class ExternalConflictException : TxfioException
{
    /// <summary>
    /// メッセージと失敗したパスを指定して例外を作る
    /// </summary>
    /// <param name="message">例外メッセージ</param>
    /// <param name="path">失敗したパス</param>
    public ExternalConflictException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// 失敗したパス
    /// </summary>
    public string Path { get; }
}
