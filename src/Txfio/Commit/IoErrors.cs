namespace Txfio;

/// <summary>
/// Tells apart exceptions from disk operations, and sorts them into failure reasons.
/// </summary>
internal static class IoErrors
{
    /// <summary>
    /// Returns whether the exception is treated as a failed disk operation.
    /// </summary>
    /// <param name="exception">The exception to check.</param>
    /// <returns><see langword="true"/> for <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</returns>
    internal static bool IsIo(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    /// <summary>
    /// Sorts an exception from a disk operation into the reason an operation failed.
    /// </summary>
    /// <param name="exception">An exception for which <see cref="IsIo"/> returns <see langword="true"/>.</param>
    /// <returns><see cref="OperationFailureReason.SharingViolation"/> for a sharing violation, otherwise <see cref="OperationFailureReason.IoFailure"/>.</returns>
    internal static OperationFailureReason Classify(Exception exception)
    {
        return exception is IOException io && PathLockSet.IsSharingViolation(io)
            ? OperationFailureReason.SharingViolation
            : OperationFailureReason.IoFailure;
    }

    /// <summary>
    /// Deletes one path.
    /// </summary>
    /// <param name="ignoreIoFailures">When <see langword="true"/>, returns <see langword="false"/> instead of throwing a failed disk operation.</param>
    /// <param name="delete">The delete to run.</param>
    /// <returns><see langword="true"/> if it was deleted.</returns>
    internal static bool TryDelete(bool ignoreIoFailures, Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (ignoreIoFailures && IsIo(exception))
        {
            return false;
        }
    }
}
