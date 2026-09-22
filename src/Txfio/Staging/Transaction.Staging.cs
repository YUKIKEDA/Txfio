namespace Txfio;

/// <content>
/// ステージング（Add / Update）
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public Task AddAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Add, path, content, cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        return StageAsync(PendingChangeKind.Update, path, content, cancellationToken);
    }

    private async Task StageAsync(
        PendingChangeKind kind,
        string path,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfCannotMutate();
        string targetPath = WorkPath.ResolveInWorkFolder(_workFolder, path);
        StagingRules.EnsureParentDirectoryExists(targetPath);

        int existingIndex = FindOperationIndex(targetPath);
        PendingChangeKind recordedKind = kind;
        if (existingIndex >= 0)
        {
            recordedKind = StagingRules.NormalizeRestageKind(_operations[existingIndex].Kind, kind);
        }
        else
        {
            StagingRules.EnsureTargetMatchesKind(kind, targetPath);
        }

        string stagingPath = WorkPath.StagingFilePath(targetPath, _transactionId);
        await StagingFile.WriteAsync(stagingPath, content, cancellationToken).ConfigureAwait(false);

        JournalOperation operation = new JournalOperation(recordedKind, targetPath, stagingPath);
        if (existingIndex >= 0)
        {
            _operations[existingIndex] = operation;
        }
        else
        {
            _operations.Add(operation);
        }

        try
        {
            await PersistAsync(committing: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (existingIndex < 0)
            {
                _operations.Remove(operation);
                StagingFile.TryDelete(stagingPath);
            }

            throw;
        }
    }
}
