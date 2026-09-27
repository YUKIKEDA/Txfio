using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingRulesTests
{
    /// <summary>
    /// The same root is not treated as crossing volumes.
    /// </summary>
    /// <remarks>
    /// <para>Given: both paths have the same root.</para>
    /// <para>When: EnsureSameVolume is called.</para>
    /// <para>Then: no exception.</para>
    /// </remarks>
    [WindowsFact("Drive-letter paths")]
    public void EnsureSameVolume_SameRootDoesNotThrow()
    {
        StagingRules.EnsureSameVolume(@"C:\work\a.txt", @"C:\work\sub\b.txt");
    }

    /// <summary>
    /// Different roots fail as crossing volumes.
    /// </summary>
    /// <remarks>
    /// <para>Given: the roots of the two paths differ.</para>
    /// <para>When: EnsureSameVolume is called.</para>
    /// <para>Then: UnsupportedOperationException.</para>
    /// </remarks>
    [WindowsFact("Drive-letter paths")]
    public void EnsureSameVolume_DifferentRootThrowsUnsupportedOperationException()
    {
        Assert.Throws<UnsupportedOperationException>(() => StagingRules.EnsureSameVolume(@"C:\work\a.txt", @"D:\work\b.txt"));
    }
}
