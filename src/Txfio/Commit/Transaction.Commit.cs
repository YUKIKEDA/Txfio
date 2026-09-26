namespace Txfio;

/// <content>
/// コミット時の検証と適用
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

        if (_paths.Rows.Count == 0)
        {
            await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);
            _locks.Release();
            ReleaseLiveness();
            _committed = true;
            return new CommitReport(CommitResult.Succeeded, Array.Empty<OperationReport>());
        }

        // 開始後に落ちたトランザクションの残骸でも、確定したデータは消さない
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

        _paths.Rows.Clear();
        _paths.Rows.AddRange(stamped);
        await PersistAsync(committing: true, CancellationToken.None).ConfigureAwait(false);
        _committingWritten = true;
        _faults.CheckPoint(IFaultInjector.AfterCommitting);

        // 適用中の例外は再送出する（Dispose はロールバックしない）
        bool conflict = !StagingApplier.TryApplyAll(_paths.Rows, _faults, out OperationReport[] skipped);

        // 衝突しても残さない（残すと、あとの Recover が他のトランザクションの確定したパスを対象にやり直す）
        if (conflict)
        {
            StagingApplier.DeleteStagingFiles(_paths.Rows);
        }

        await JournalStore.DeleteAsync(_journalPath).ConfigureAwait(false);

        _locks.Release();
        ReleaseLiveness();
        _committed = true;
        _paths.Rows.Clear();
        CommitResult result = conflict ? CommitResult.PartialConflict : CommitResult.Succeeded;
        IReadOnlyList<OperationReport> operations = conflict ? skipped : Array.Empty<OperationReport>();
        return new CommitReport(result, operations);
    }
}
