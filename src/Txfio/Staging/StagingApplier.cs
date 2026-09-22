namespace Txfio;

/// <summary>
/// ステージングファイルを対象パスへ昇格する
/// </summary>
internal static class StagingApplier
{
    /// <summary>
    /// `.txnew` を対象パスへ Move する（既に適用済みなら <see langword="true"/>、失敗なら <see langword="false"/>）
    /// </summary>
    /// <param name="operation">適用する操作</param>
    /// <returns>適用できた、または既に適用済みなら <see langword="true"/></returns>
    internal static bool TryApply(JournalOperation operation)
    {
        if (!File.Exists(operation.StagingPath))
        {
            return File.Exists(operation.Path);
        }

        try
        {
            bool overwrite = operation.Kind == PendingChangeKind.Update;
            File.Move(operation.StagingPath, operation.Path, overwrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
