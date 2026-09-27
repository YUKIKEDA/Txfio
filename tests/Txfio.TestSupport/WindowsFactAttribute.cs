using Xunit;

namespace Txfio.Tests.Support;

/// <summary>
/// A Fact that runs only on Windows. On other operating systems it is skipped with a reason.
/// </summary>
/// <remarks>
/// Use it for tests that check Windows behavior itself, such as junctions, drive letters, and open files that cannot be deleted.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsFactAttribute"/> class, which skips with the reason on operating systems other than Windows.
    /// </summary>
    /// <param name="reason">Why the test is Windows only.</param>
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only: " + reason;
        }
    }
}
