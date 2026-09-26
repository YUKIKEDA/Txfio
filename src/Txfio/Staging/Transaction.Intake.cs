namespace Txfio;

/// <content>
/// ディレクトリの取り込み（木の計画と、ジャーナルを書いたあとの実体作成）
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

    private bool SkipIntakeEntry(string entry)
    {
        return WorkPath.IsReparsePoint(entry) || WorkPath.IsThisTransactionStagingFile(entry, _transactionId);
    }

    private void ForEachTreeChild(
        string current,
        CancellationToken cancellationToken,
        Action<string> onDirectory,
        Action<string> onFile)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SkipIntakeEntry(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                onDirectory(entry);
                continue;
            }

            if (File.Exists(entry))
            {
                onFile(entry);
            }
        }
    }

    private void PlanCopiedTree(
        string sourceRoot,
        string current,
        string destinationRoot,
        List<string> directories,
        List<PlannedTreeFile> files,
        CancellationToken cancellationToken)
    {
        ForEachTreeChild(
            current,
            cancellationToken,
            entry =>
            {
                directories.Add(MapTreeDestination(sourceRoot, destinationRoot, entry));
                PlanCopiedTree(sourceRoot, entry, destinationRoot, directories, files, cancellationToken);
            },
            entry => files.Add(new PlannedTreeFile(entry, MapTreeDestination(sourceRoot, destinationRoot, entry))));
    }

    private bool PlanArchivedTree(
        string sourceRoot,
        string current,
        string prefix,
        List<PlannedArchiveEntry> planned,
        CancellationToken cancellationToken)
    {
        bool plannedAny = false;
        ForEachTreeChild(
            current,
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
        int operationCount = _paths.Rows.Count;
        int directoryCount = _createdDirectories.Count;
        foreach (string directory in directories)
        {
            _createdDirectories.Add(directory);
        }

        foreach (string filePath in filePaths)
        {
            string stagingPath = WorkPath.StagingFilePath(filePath, _transactionId);
            _paths.Rows.Add(new JournalOperation(PendingChangeKind.Add, filePath, stagingPath));
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
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
        }
        catch
        {
            RollbackAddedOperations(operationCount);
            DeleteCreatedDirectoriesFrom(directoryCount, ignoreIoFailures: true);
            await TryPersistUndoAsync().ConfigureAwait(false);
            throw;
        }
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
