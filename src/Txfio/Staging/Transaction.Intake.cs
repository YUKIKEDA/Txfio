namespace Txfio;

/// <content>
/// Directory intake (planning the tree, and creating it after the journal is written).
/// </content>
internal sealed partial class Transaction
{
    private static string MapTreeDestination(string sourceRoot, string destinationRoot, string entry)
    {
        string relative = System.IO.Path.GetRelativePath(sourceRoot, entry);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(destinationRoot, relative));
    }

    private static string ArchiveEntryName(string sourceRoot, string prefix, string entry)
    {
        return prefix
            + System.IO.Path.GetRelativePath(sourceRoot, entry)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');
    }

    // Walks inside the work folder skip reparse points and the staging entries of every transaction (a directory .txold with everything under it).
    // Use the attributes from enumeration, instead of asking the disk for each entry.
    private static bool SkipIntakeEntry(FileSystemInfo entry)
    {
        return (entry.Attributes & FileAttributes.ReparsePoint) != 0
            || StagingNames.IsMatch(entry.Name);
    }

    // An Import source is outside the work folder, so a staging name there is rejected instead of skipped.
    private static void ThrowIfImportSourceHasStagingName(string path)
    {
        if (StagingNames.IsMatch(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path))))
        {
            throw new InvalidOperationException("An import source with a staging name cannot be used: " + path);
        }
    }

    private static void ForEachTreeChild(
        string current,
        bool rejectStagingNames,
        CancellationToken cancellationToken,
        Action<string> onDirectory,
        Action<string> onFile)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rejectStagingNames)
            {
                ThrowIfImportSourceHasStagingName(entry.FullName);
            }

            if (SkipIntakeEntry(entry))
            {
                continue;
            }

            if (entry is DirectoryInfo)
            {
                onDirectory(entry.FullName);
                continue;
            }

            onFile(entry.FullName);
        }
    }

    private static void PlanCopiedTree(
        string sourceRoot,
        string current,
        string destinationRoot,
        bool rejectStagingNames,
        List<string> directories,
        List<PlannedTreeFile> files,
        CancellationToken cancellationToken)
    {
        ForEachTreeChild(
            current,
            rejectStagingNames,
            cancellationToken,
            entry =>
            {
                directories.Add(MapTreeDestination(sourceRoot, destinationRoot, entry));
                PlanCopiedTree(sourceRoot, entry, destinationRoot, rejectStagingNames, directories, files, cancellationToken);
            },
            entry => files.Add(new PlannedTreeFile(entry, MapTreeDestination(sourceRoot, destinationRoot, entry))));
    }

    private static bool PlanArchivedTree(
        string sourceRoot,
        string current,
        string prefix,
        List<PlannedArchiveEntry> planned,
        CancellationToken cancellationToken)
    {
        bool plannedAny = false;
        ForEachTreeChild(
            current,
            rejectStagingNames: false,
            cancellationToken,
            entry =>
            {
                string entryName = ArchiveEntryName(sourceRoot, prefix, entry);
                if (!PlanArchivedTree(sourceRoot, entry, prefix, planned, cancellationToken))
                {
                    planned.Add(new PlannedArchiveEntry(entryName + "/", null));
                }

                plannedAny = true;
            },
            entry =>
            {
                planned.Add(new PlannedArchiveEntry(ArchiveEntryName(sourceRoot, prefix, entry), entry));
                plannedAny = true;
            });
        return plannedAny;
    }

    private async Task ApplyCopiedTreeAsync(
        IReadOnlyList<string> directories,
        IReadOnlyList<PlannedTreeFile> files,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string[] destinations = new string[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            destinations[i] = files[i].DestinationPath;
        }

        await ApplyStagedTreeAsync(
                directories,
                destinations,
                (index, tracker, token) => WriteCopySourceAsync(
                    files[index].SourcePath,
                    WorkPath.StagingFilePath(files[index].DestinationPath, _transactionId),
                    tracker,
                    token),
                progress,
                totalBytes: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyStagedTreeAsync(
        IReadOnlyList<string> directories,
        IReadOnlyList<string> filePaths,
        Func<int, IProgress<TransferProgress>, CancellationToken, Task<long>> writeFile,
        IProgress<TransferProgress>? progress,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        int directoryCount = _createdDirectories.Count;
        List<JournalOperation> added = new List<JournalOperation>(filePaths.Count);
        foreach (string filePath in filePaths)
        {
            added.Add(new JournalOperation(PendingChangeKind.Add, filePath, WorkPath.StagingFilePath(filePath, _transactionId)));
        }

        await RecordThenMaterializeAsync(
                () =>
                {
                    _createdDirectories.AddRange(directories);
                    foreach (JournalOperation operation in added)
                    {
                        _paths.Add(operation);
                    }
                },
                async () =>
                {
                    foreach (string directory in directories)
                    {
                        Directory.CreateDirectory(directory);
                    }

                    DirectoryCopyProgress tracker = new DirectoryCopyProgress(progress, totalBytes);
                    for (int i = 0; i < filePaths.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long bytes = await writeFile(i, tracker, cancellationToken).ConfigureAwait(false);
                        tracker.CompleteFile(bytes);
                    }

                    if (!tracker.Reported)
                    {
                        progress?.Report(new TransferProgress(0, totalBytes));
                    }
                },
                () =>
                {
                    foreach (JournalOperation operation in added)
                    {
                        TryCleanup(() => StagingFile.TryDelete(operation.StagingPath));
                    }

                    DeleteCreatedDirectoriesFrom(directoryCount, ignoreIoFailures: true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class PlannedTreeFile
    {
        internal PlannedTreeFile(string sourcePath, string destinationPath)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
        }

        internal string SourcePath { get; }

        internal string DestinationPath { get; }
    }

    private sealed class DirectoryCopyProgress : IProgress<TransferProgress>
    {
        private readonly IProgress<TransferProgress>? _inner;
        private readonly long? _totalBytes;
        private long _completed;

        internal DirectoryCopyProgress(IProgress<TransferProgress>? inner, long? totalBytes = null)
        {
            _inner = inner;
            _totalBytes = totalBytes;
        }

        internal bool Reported { get; private set; }

        /// <inheritdoc />
        public void Report(TransferProgress value)
        {
            Reported = true;
            _inner?.Report(new TransferProgress(_completed + value.BytesCopied, _totalBytes));
        }

        internal void CompleteFile(long bytes)
        {
            _completed += bytes;
        }
    }
}
