namespace Txfio;

/// <summary>
/// テスト用にコミットを途中で止めたことを表す例外
/// </summary>
internal sealed class CrashInjectionException : Exception
{
    /// <summary>
    /// 止めた地点を指定して例外を作る
    /// </summary>
    /// <param name="name">止めた地点</param>
    internal CrashInjectionException(string name)
        : base("Commit stopped partway: " + name)
    {
        Name = name;
    }

    /// <summary>
    /// 止めた地点
    /// </summary>
    internal string Name { get; }
}
