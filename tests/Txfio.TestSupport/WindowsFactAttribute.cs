using Xunit;

namespace Txfio.Tests.Support;

/// <summary>
/// Windows でだけ回す Fact。ほかの OS では理由を付けて Skip にする
/// </summary>
/// <remarks>
/// ジャンクション、ドライブ文字、開いたファイルを消せないことなど、Windows の挙動そのものを確かめるテストに付ける
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>
    /// Windows 以外なら理由を付けて Skip にする
    /// </summary>
    /// <param name="reason">Windows 専用である理由</param>
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows 専用: " + reason;
        }
    }
}
