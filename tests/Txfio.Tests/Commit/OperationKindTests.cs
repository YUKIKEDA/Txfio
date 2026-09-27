namespace Txfio.Tests.Commit;

public sealed class OperationKindTests
{
    /// <summary>
    /// Every operation kind has an implementation, and an unknown kind throws instead of being skipped silently.
    /// </summary>
    /// <remarks>
    /// <para>Given: the defined values of PendingChangeKind, and an undefined value 99.</para>
    /// <para>When: OperationKind.For is called with each.</para>
    /// <para>Then: defined values return the implementation for that kind, and 99 throws InvalidOperationException.</para>
    /// </remarks>
    [Fact]
    public void For_ReturnsImplementationForDefinedKindsAndThrowsForUnknown()
    {
        foreach (PendingChangeKind kind in Enum.GetValues<PendingChangeKind>())
        {
            Assert.Equal(kind, OperationKind.For(kind).Kind);
        }

        Assert.Throws<InvalidOperationException>(() => OperationKind.For((PendingChangeKind)99));
    }
}
