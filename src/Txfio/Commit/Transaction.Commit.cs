namespace Txfio;

/// <content>
/// Checks and apply at commit.
/// </content>
internal sealed partial class Transaction
{
    /// <inheritdoc />
    public async Task<CommitReport> CommitAsync(CancellationToken cancellationToken = default)
    {
        using CallScope scope = EnterCall();
        await CallerContext.LeaveAsync();
        ThrowIfCannotMutate();
        cancellationToken.ThrowIfCancellationRequested();

        if (_paths.Count == 0)
        {
            await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
            _locks.Release();
            ReleaseLiveness();
            _committed = true;
            return new CommitReport(CommitResult.Succeeded, Array.Empty<OperationReport>());
        }

        // Even for leftovers of a transaction that crashed after it began, committed data is not deleted.
        StaleJournals.ThrowIfAny(_workFolder);

        Func<JournalOperation, bool>? isExternalChange = _externalChanges is null
            ? null
            : _externalChanges.IsMismatch;
        if (!OperationOutcomes.TryStamp(
                _paths.Rows,
                _transactionId,
                out JournalOperation[] stamped,
                out OperationReport[] rejections,
                isExternalChange))
        {
            return new CommitReport(CommitResult.Failed, rejections);
        }

        _paths.Load(stamped);
        await PersistAsync(committing: true, CancellationToken.None).ConfigureAwait(false);
        _committingWritten = true;
        _faults.CheckPoint(IFaultInjector.AfterCommitting);

        // An exception during apply is rethrown (Dispose does not roll back).
        bool conflict = !StagingApplier.TryApplyAll(_paths.Rows, _faults, out OperationReport[] skipped);

        // Do not keep the journal even on conflict (if it stayed, a later Recover would redo paths that other transactions have committed).
        if (conflict)
        {
            StagingApplier.DeleteStagingFiles(_paths.Rows);
        }

        await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);

        _locks.Release();
        ReleaseLiveness();
        _committed = true;
        _paths.Clear();
        CommitResult result = conflict ? CommitResult.PartialConflict : CommitResult.Succeeded;
        IReadOnlyList<OperationReport> operations = conflict ? skipped : Array.Empty<OperationReport>();
        return new CommitReport(result, operations);
    }
}
