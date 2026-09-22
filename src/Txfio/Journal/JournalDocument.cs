using System.Text.Json.Serialization;

namespace Txfio;

internal sealed class JournalDocument
{
    [JsonConstructor]
    public JournalDocument(int version, Guid transactionId, bool committing)
    {
        this.Version = version;
        this.TransactionId = transactionId;
        this.Committing = committing;
    }

    public int Version { get; }

    public Guid TransactionId { get; }

    public bool Committing { get; }
}
