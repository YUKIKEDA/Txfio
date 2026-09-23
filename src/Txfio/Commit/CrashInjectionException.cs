namespace Txfio;

/// <summary>
/// クラッシュ注入でコミットを止めたときに投げる例外
/// </summary>
internal sealed class CrashInjectionException : Exception
{
    /// <summary>
    /// 止めた地点を指定して例外を作る
    /// </summary>
    /// <param name="name">止めた地点</param>
    internal CrashInjectionException(string name)
        : base("クラッシュを注入しました: " + name)
    {
        Name = name;
    }

    /// <summary>
    /// 止めた地点
    /// </summary>
    internal string Name { get; }
}
