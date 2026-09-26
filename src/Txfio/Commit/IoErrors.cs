namespace Txfio;

/// <summary>
/// ファイル操作で起きる例外の見分けと、失敗の理由への振り分け
/// </summary>
internal static class IoErrors
{
    /// <summary>
    /// ディスク操作の失敗として扱う例外かどうかを判定する
    /// </summary>
    /// <param name="exception">調べる例外</param>
    /// <returns><see cref="IOException"/> か <see cref="UnauthorizedAccessException"/> なら <see langword="true"/></returns>
    internal static bool IsIo(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    /// <summary>
    /// ディスク操作の例外を、操作の失敗の理由にする
    /// </summary>
    /// <param name="exception"><see cref="IsIo"/> が <see langword="true"/> になる例外</param>
    /// <returns>共有違反なら <see cref="OperationFailureReason.SharingViolation"/>、それ以外は <see cref="OperationFailureReason.IoFailure"/></returns>
    internal static OperationFailureReason Classify(Exception exception)
    {
        return exception is IOException io && PathLockSet.IsSharingViolation(io)
            ? OperationFailureReason.SharingViolation
            : OperationFailureReason.IoFailure;
    }

    /// <summary>
    /// 1 件消す
    /// </summary>
    /// <param name="ignoreIoFailures"><see langword="true"/> なら、ディスク操作の失敗を投げずに <see langword="false"/> を返す</param>
    /// <param name="delete">消す処理</param>
    /// <returns>消せたら <see langword="true"/></returns>
    internal static bool TryDelete(bool ignoreIoFailures, Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (ignoreIoFailures && IsIo(exception))
        {
            return false;
        }
    }
}
