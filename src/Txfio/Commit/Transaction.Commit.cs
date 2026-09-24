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
            _locks.Release();
            ReleaseLiveness();
            _committed = true;
            return CommitResult.Succeeded;
        }

        // 開始のあとで落ちたトランザクションの残骸にも、確定したデータを消させない
        StaleJournals.ThrowIfAny(_workFolder);

        if (!OperationOutcomes.TryStamp(_operations, _transactionId, out JournalOperation[] stamped))
        {
            return CommitResult.Failed;
        }

        _operations.Clear();
        _operations.AddRange(stamped);
        await PersistAsync(committing: true, CancellationToken.None).ConfigureAwait(false);
        CrashInjector.CheckPoint(CrashInjector.AfterCommitting);

        bool conflict = !StagingApplier.TryApplyAll(_operations);

        // 衝突しても残さない。残すと、あとの Recover が他のトランザクションの確定したパスへやり直す
        if (conflict)
        {
            StagingApplier.DeleteStagingFiles(_operations);
        }

        await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);

        _locks.Release();
        ReleaseLiveness();
        _committed = true;
        _operations.Clear();
        return conflict ? CommitResult.PartialConflict : CommitResult.Succeeded;
    }
}
