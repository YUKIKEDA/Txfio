namespace Txfio;

internal sealed class Transaction : ITransaction
{
    private readonly string journalPath;
    private bool committed;
    private bool disposed;

    internal Transaction(string journalPath)
    {
        this.journalPath = journalPath;
    }

    public async Task<CommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.committed)
        {
            throw new InvalidOperationException("このトランザクションは既にコミット済みです");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await JournalStore.DeleteAsync(this.journalPath).ConfigureAwait(false);
        this.committed = true;
        return CommitResult.Succeeded;
    }

    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        return Array.Empty<PendingChange>();
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (!this.committed)
        {
            await JournalStore.DeleteAsync(this.journalPath).ConfigureAwait(false);
        }
    }
}
