namespace Txfio;

/// <summary>
/// ワークフォルダに対する 1 件のトランザクション
/// </summary>
internal sealed class Transaction : ITransaction
{
    private readonly string journalPath;
    private bool committed;
    private bool disposed;

    /// <summary>
    /// 指定したジャーナルパスでトランザクションを開始する
    /// </summary>
    /// <param name="journalPath">このトランザクションのジャーナルファイル</param>
    internal Transaction(string journalPath)
    {
        this.journalPath = journalPath;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        return Array.Empty<PendingChange>();
    }

    /// <inheritdoc />
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
