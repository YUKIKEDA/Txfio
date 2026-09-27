namespace Txfio;

/// <summary>
/// Precondition checks for Add / Update / Delete / Move / CreateDirectory / DeleteTree.
/// </summary>
internal static class StagingRules
{
    /// <summary>
    /// Checks whether the target file of Add / Update / Delete exists.
    /// </summary>
    /// <param name="kind">The operation kind.</param>
    /// <param name="targetPath">The target path.</param>
    internal static void EnsureTargetMatchesKind(PendingChangeKind kind, string targetPath)
    {
        if (kind == PendingChangeKind.Delete)
        {
            if (!File.Exists(targetPath))
            {
                throw new ExternalConflictException("The file to delete does not exist: " + targetPath, targetPath);
            }

            return;
        }

        if (Directory.Exists(targetPath))
        {
            // Writing a file to a directory would not fail until the commit check.
            string message = kind == PendingChangeKind.Add
                ? "A directory already exists at the path to add: "
                : "The path to update is a directory: ";
            throw new ExternalConflictException(message + targetPath, targetPath);
        }

        bool exists = File.Exists(targetPath);
        if (kind == PendingChangeKind.Add && exists)
        {
            throw new ExternalConflictException("The file to add already exists: " + targetPath, targetPath);
        }

        if (kind == PendingChangeKind.Update && !exists)
        {
            throw new ExternalConflictException("The file to update does not exist: " + targetPath, targetPath);
        }
    }

    /// <summary>
    /// Rejects overlapping operations under the source and destination when swapping a directory (if the source is a created directory, only Adds under it are allowed).
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="createdDirectories">The directories this transaction created.</param>
    /// <param name="sourcePath">The source.</param>
    /// <param name="destPath">The destination.</param>
    internal static void ThrowIfDirectoryReplaceConflicts(
        IReadOnlyList<JournalOperation> operations,
        IReadOnlyList<string> createdDirectories,
        string sourcePath,
        string destPath)
    {
        if (IsInsideDirectory(sourcePath, destPath) || IsInsideDirectory(destPath, sourcePath))
        {
            throw new InvalidOperationException("A directory cannot be swapped with its own descendant or parent: " + sourcePath);
        }

        bool createdSource = createdDirectories.Any(
            directory => string.Equals(directory, sourcePath, StringComparison.OrdinalIgnoreCase));
        foreach (JournalOperation operation in operations)
        {
            if (IsInsideDirectory(destPath, operation.Path)
                || (operation.NewPath is not null && IsInsideDirectory(destPath, operation.NewPath)))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }

            bool underSource = IsInsideDirectory(sourcePath, operation.Path)
                || (operation.NewPath is not null && IsInsideDirectory(sourcePath, operation.NewPath));
            if (underSource && !(createdSource && operation.Kind == PendingChangeKind.Add))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// No further operation is allowed on the source or destination of a replacing or swapping Move, or under the destination of a swap.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="path">The path to operate on.</param>
    internal static void ThrowIfOverwriteMovePath(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Move
                && operation.Overwrite
                && (string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(operation.NewPath, path, StringComparison.OrdinalIgnoreCase)
                    || (operation.NewPath is not null && IsInsideDirectory(operation.NewPath, path))))
            {
                throw new InvalidOperationException("No further operation is allowed on the source or destination of a replacing or swapping Move, or under the destination of a swap: " + path);
            }
        }
    }

    /// <summary>
    /// Checks that the source is an existing file.
    /// </summary>
    /// <param name="sourcePath">The source path.</param>
    internal static void EnsureMoveSourceExists(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new ExternalConflictException("The source file does not exist: " + sourcePath, sourcePath);
        }
    }

    /// <summary>
    /// Checks that neither a file nor a directory exists at the destination.
    /// </summary>
    /// <param name="destPath">The destination path.</param>
    internal static void EnsureMoveDestinationIsFree(string destPath)
    {
        if (Directory.Exists(destPath))
        {
            throw new ExternalConflictException("The destination is a directory: " + destPath, destPath);
        }

        if (File.Exists(destPath))
        {
            throw new ExternalConflictException("The destination file already exists: " + destPath, destPath);
        }
    }

    /// <summary>
    /// Checks whether the source and destination have the same root (drive or share).
    /// </summary>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destPath">The destination path.</param>
    /// <remarks>Mount points are not checked (path resolution rejects reparse points inside the work folder).</remarks>
    internal static void EnsureSameVolume(string sourcePath, string destPath)
    {
        string? sourceRoot = System.IO.Path.GetPathRoot(sourcePath);
        string? destRoot = System.IO.Path.GetPathRoot(destPath);
        if (string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(destRoot)
            || !string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedOperationException("A move across volumes is not supported: " + sourcePath + " -> " + destPath);
        }
    }

    /// <summary>
    /// Checks whether the target's parent directory exists.
    /// </summary>
    /// <param name="targetPath">The target path.</param>
    internal static void EnsureParentDirectoryExists(string targetPath)
    {
        string? parent = System.IO.Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            string reported = string.IsNullOrEmpty(parent) ? targetPath : parent;
            throw new ExternalConflictException("The parent directory does not exist: " + reported, reported);
        }
    }

    /// <summary>
    /// Excludes the metadata folder and everything under it from operations.
    /// </summary>
    /// <param name="workFolder">The work folder.</param>
    /// <param name="targetPath">The target path.</param>
    internal static void EnsureNotMetadataFolder(string workFolder, string targetPath)
    {
        if (WorkPath.IsInMetadataFolder(workFolder, targetPath))
        {
            throw new InvalidOperationException("The metadata folder and paths under it cannot be used: " + targetPath);
        }
    }

    /// <summary>
    /// Rejects operations under the source or destination of a directory Move.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="path">The path about to be operated on.</param>
    internal static void ThrowIfInsideDirectoryMove(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind != PendingChangeKind.Move || !operation.IsDirectory || operation.NewPath is null)
            {
                continue;
            }

            if (IsInsideDirectory(operation.Path, path) || IsInsideDirectory(operation.NewPath, path))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// Rejects moving a directory under itself, and operations that overlap under the source or destination.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="sourcePath">The directory to move.</param>
    /// <param name="destPath">The destination.</param>
    internal static void ThrowIfDirectoryMoveConflicts(
        IReadOnlyList<JournalOperation> operations,
        string sourcePath,
        string destPath)
    {
        if (IsInsideDirectory(sourcePath, destPath))
        {
            throw new InvalidOperationException("A directory cannot be moved under itself: " + sourcePath);
        }

        foreach (JournalOperation operation in operations)
        {
            if (IsInsideDirectory(sourcePath, operation.Path) || IsInsideDirectory(destPath, operation.Path))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }

            if (operation.NewPath is not null
                && (IsInsideDirectory(sourcePath, operation.NewPath) || IsInsideDirectory(destPath, operation.NewPath)))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// Rejects copying to the same path, or copying a directory under itself.
    /// </summary>
    /// <param name="sourcePath">The source.</param>
    /// <param name="destPath">The destination.</param>
    internal static void ThrowIfCopyDestinationInsideSource(string sourcePath, string destPath)
    {
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A path cannot be copied to itself: " + sourcePath);
        }

        if (IsInsideDirectory(sourcePath, destPath))
        {
            throw new InvalidOperationException("A directory cannot be copied under itself: " + sourcePath);
        }
    }

    /// <summary>
    /// Rejects a ZIP output that is the input itself or under the input directory.
    /// </summary>
    /// <param name="sourcePath">The input.</param>
    /// <param name="archivePath">The ZIP output path.</param>
    internal static void ThrowIfArchiveInsideSource(string sourcePath, string archivePath)
    {
        if (string.Equals(sourcePath, archivePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A ZIP cannot be created at the same path as its input: " + sourcePath);
        }

        if (IsInsideDirectory(sourcePath, archivePath))
        {
            throw new InvalidOperationException("A ZIP cannot be created under its input directory: " + sourcePath);
        }
    }

    /// <summary>
    /// Rejects later operations on a directory scheduled for deletion.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="path">The path about to be operated on.</param>
    internal static void ThrowIfTouchesDeletedDirectory(IReadOnlyList<JournalOperation> operations, string path)
    {
        if (IsPendingDirectoryDelete(operations, path)
            || IsPendingDirectoryDelete(operations, System.IO.Path.GetDirectoryName(path)))
        {
            throw new InvalidOperationException("This path is already staged by another operation");
        }
    }

    /// <summary>
    /// Rejects operations on a directory created by CreateDirectory itself.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="path">The path about to be operated on.</param>
    internal static void ThrowIfCreateDirectoryPath(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.CreateDirectory
                && string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// Rejects operations under a directory scheduled for DeleteTree.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="path">The path about to be operated on.</param>
    internal static void ThrowIfInsideDeleteTree(IReadOnlyList<JournalOperation> operations, string path)
    {
        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.DeleteTree && IsInsideDirectory(operation.Path, path))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// Rejects when this transaction has an operation under the directory to delete with DeleteTree.
    /// </summary>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="directoryPath">The directory to delete with DeleteTree.</param>
    internal static void ThrowIfOperationUnderDirectory(IReadOnlyList<JournalOperation> operations, string directoryPath)
    {
        foreach (JournalOperation operation in operations)
        {
            if (IsInsideDirectory(directoryPath, operation.Path)
                || (operation.NewPath is not null && IsInsideDirectory(directoryPath, operation.NewPath)))
            {
                throw new InvalidOperationException("This path is already staged by another operation");
            }
        }
    }

    /// <summary>
    /// Checks the direct-children conditions of a directory delete (throws if they are not met).
    /// </summary>
    /// <param name="directoryPath">The target directory.</param>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="transactionId">The ID of this transaction.</param>
    internal static void EnsureDirectoryDeleteAllowed(
        string directoryPath,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId)
    {
        if (!MatchesDirectoryDeletePreconditions(directoryPath, operations, transactionId))
        {
            throw new ExternalConflictException("The directory has a child that is not scheduled: " + directoryPath, directoryPath);
        }
    }

    /// <summary>
    /// Returns whether the direct-children conditions of a directory delete are met.
    /// </summary>
    /// <param name="directoryPath">The target directory.</param>
    /// <param name="operations">The current list of operations.</param>
    /// <param name="transactionId">The ID of this transaction.</param>
    /// <returns><see langword="true"/> if the directory has no direct children, or only scheduled ones.</returns>
    internal static bool MatchesDirectoryDeletePreconditions(
        string directoryPath,
        IReadOnlyList<JournalOperation> operations,
        Guid transactionId)
    {
        if (!Directory.Exists(directoryPath) || File.Exists(directoryPath))
        {
            return false;
        }

        foreach (JournalOperation operation in operations)
        {
            if (IsImmediateChild(directoryPath, operation.Path))
            {
                if (operation.Kind == PendingChangeKind.Add
                    || operation.Kind == PendingChangeKind.Update
                    || operation.Kind == PendingChangeKind.CreateDirectory)
                {
                    return false;
                }

                if (operation.Kind == PendingChangeKind.Move
                    && IsMoveIntoDirectory(directoryPath, operation.NewPath))
                {
                    return false;
                }
            }

            if (operation.Kind == PendingChangeKind.Move
                && IsMoveIntoDirectory(directoryPath, operation.NewPath)
                && !string.Equals(operation.Path, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directoryPath);
        }
        catch (IOException)
        {
            return false;
        }

        foreach (string entry in entries)
        {
            if (WorkPath.IsThisTransactionStagingFile(entry, transactionId))
            {
                continue;
            }

            if (!IsChildAccountedForDelete(directoryPath, entry, operations))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideDirectory(string directoryPath, string path)
    {
        return PathMath.IsUnder(directoryPath, path);
    }

    private static bool IsPendingDirectoryDelete(IReadOnlyList<JournalOperation> operations, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (JournalOperation operation in operations)
        {
            if (operation.Kind == PendingChangeKind.Delete
                && operation.IsDirectory
                && string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsImmediateChild(string parentDirectory, string path)
    {
        string? parent = System.IO.Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(parent)
            && string.Equals(parent, parentDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMoveIntoDirectory(string directoryPath, string? destPath)
    {
        if (string.IsNullOrEmpty(destPath))
        {
            return false;
        }

        return string.Equals(destPath, directoryPath, StringComparison.OrdinalIgnoreCase)
            || IsImmediateChild(directoryPath, destPath);
    }

    private static bool IsChildAccountedForDelete(
        string directoryPath,
        string childPath,
        IReadOnlyList<JournalOperation> operations)
    {
        foreach (JournalOperation operation in operations)
        {
            if (!string.Equals(operation.Path, childPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (operation.Kind == PendingChangeKind.Delete)
            {
                return true;
            }

            if (operation.Kind == PendingChangeKind.Move
                && !IsMoveIntoDirectory(directoryPath, operation.NewPath))
            {
                return true;
            }

            return false;
        }

        return false;
    }
}
