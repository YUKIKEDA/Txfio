namespace Txfio;

/// <content>
/// コミット時の検証と適用
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task<CommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.Count == 0)
        {
            await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
            _committed = true;
            return CommitResult.Succeeded;
        }

        if (!TryValidateForCommit())
        {
            return CommitResult.Failed;
        }

        await PersistAsync(committing: true, CancellationToken.None).ConfigureAwait(false);

        bool conflict = !StagingApplier.TryApplyAll(_operations);

        if (!conflict)
        {
            await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
        }

        _committed = true;
        _operations.Clear();
        return conflict ? CommitResult.PartialConflict : CommitResult.Succeeded;
    }

    private bool TryValidateForCommit()
    {
        foreach (JournalOperation operation in _operations)
        {
            if (operation.Kind == PendingChangeKind.Delete)
            {
                if (!File.Exists(operation.Path))
                {
                    return false;
                }

                continue;
            }

            if (string.IsNullOrEmpty(operation.StagingPath) || !File.Exists(operation.StagingPath))
            {
                return false;
            }

            bool targetExists = File.Exists(operation.Path);
            if (operation.Kind == PendingChangeKind.Add && targetExists)
            {
                return false;
            }

            if (operation.Kind == PendingChangeKind.Update && !targetExists)
            {
                return false;
            }
        }

        return true;
    }
}
