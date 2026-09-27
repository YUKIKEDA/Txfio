namespace Txfio.Tests.Stress;

/// <summary>
/// The kinds of operations used in random directory sequences.
/// </summary>
internal enum DirectoryOperationKind
{
    /// <summary>
    /// CreateDirectoryAsync on a missing path.
    /// </summary>
    CreateDirectory,

    /// <summary>
    /// AddAsync on a missing path.
    /// </summary>
    Add,

    /// <summary>
    /// UpdateAsync on an existing file.
    /// </summary>
    Update,

    /// <summary>
    /// DeleteAsync of a file or an empty directory.
    /// </summary>
    Delete,

    /// <summary>
    /// DeleteTreeAsync of a directory.
    /// </summary>
    DeleteTree,

    /// <summary>
    /// MoveAsync without overwrite.
    /// </summary>
    Move,

    /// <summary>
    /// ReadAsync of an existing file.
    /// </summary>
    Read,
}
