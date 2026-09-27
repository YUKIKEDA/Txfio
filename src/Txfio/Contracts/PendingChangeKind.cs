namespace Txfio;

/// <summary>
/// The kind of operation recorded in the journal.
/// </summary>
public enum PendingChangeKind
{
    /// <summary>
    /// Adding a new file.
    /// </summary>
    Add = 0,

    /// <summary>
    /// Updating an existing file.
    /// </summary>
    Update = 1,

    /// <summary>
    /// A scheduled delete.
    /// </summary>
    Delete = 2,

    /// <summary>
    /// A move or rename within one volume.
    /// </summary>
    Move = 3,

    /// <summary>
    /// A scheduled delete of a directory and everything under it.
    /// </summary>
    DeleteTree = 4,

    /// <summary>
    /// An empty directory created when the method is called (operations under it are separate entries).
    /// </summary>
    CreateDirectory = 5,
}
