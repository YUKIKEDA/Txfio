namespace Txfio;

/// <summary>
/// 持ち主のいない残骸ジャーナルが残っている。<see cref="Txfio.RecoverAsync"/> を呼べば解消する
/// </summary>
public sealed class RecoveryRequiredException : TxfioException
{
    /// <summary>
    /// メッセージとワークフォルダを指定して例外を作る
    /// </summary>
    /// <param name="message">例外メッセージ</param>
    /// <param name="path">ワークフォルダ</param>
    public RecoveryRequiredException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>
    /// ワークフォルダ
    /// </summary>
    public string Path { get; }
}
