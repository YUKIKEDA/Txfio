namespace Txfio.Tests.Stress;

/// <summary>
/// The operation kinds of random sequences that cross the work folder boundary.
/// </summary>
internal enum TransferKind
{
    /// <summary>
    /// Copies from inside to inside the work folder.
    /// </summary>
    Copy,

    /// <summary>
    /// Imports from outside into the work folder.
    /// </summary>
    Import,

    /// <summary>
    /// Exports from inside the work folder to outside.
    /// </summary>
    Export,
}
