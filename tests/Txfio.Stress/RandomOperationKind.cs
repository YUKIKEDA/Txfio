namespace Txfio.Tests.Stress;

/// <summary>
/// The kinds of operations used in random sequences.
/// </summary>
internal enum RandomOperationKind
{
    /// <summary>
    /// AddAsync on a missing path.
    /// </summary>
    Add,

    /// <summary>
    /// UpdateAsync on an existing path.
    /// </summary>
    Update,

    /// <summary>
    /// DeleteAsync of an existing file.
    /// </summary>
    Delete,

    /// <summary>
    /// MoveAsync from an existing file to a missing path.
    /// </summary>
    Move,

    /// <summary>
    /// ReadAsync of an existing file.
    /// </summary>
    Read,
}
