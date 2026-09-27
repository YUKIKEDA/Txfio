namespace Txfio;

/// <summary>
/// Why an operation could not be finished.
/// </summary>
public enum OperationFailureReason
{
    /// <summary>
    /// The target does not exist.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// It already exists.
    /// </summary>
    AlreadyExists = 1,

    /// <summary>
    /// It was swapped for a file or a directory.
    /// </summary>
    ReplacedByFile = 2,

    /// <summary>
    /// The direct-children conditions of a directory are not met.
    /// </summary>
    DirectoryPreconditions = 3,

    /// <summary>
    /// It matches neither Before nor After.
    /// </summary>
    BeforeAfterMismatch = 4,

    /// <summary>
    /// A sharing violation.
    /// </summary>
    SharingViolation = 5,

    /// <summary>
    /// Another IO failure.
    /// </summary>
    IoFailure = 6,

    /// <summary>
    /// The size or last write time of the recorded real file differs.
    /// </summary>
    ExternalChange = 7,

    /// <summary>
    /// The target of an Update or a file Delete has the read-only attribute.
    /// </summary>
    ReadOnly = 8,
}
