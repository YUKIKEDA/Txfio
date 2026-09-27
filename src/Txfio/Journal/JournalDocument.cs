using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// The JSON structure written to the journal file.
/// </summary>
internal sealed class JournalDocument
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalDocument"/> class.
    /// </summary>
    /// <param name="version">The version of the document format.</param>
    /// <param name="transactionId">The ID of the transaction.</param>
    /// <param name="committing"><see langword="true"/> while the commit is being applied.</param>
    /// <param name="operations">The list of unfinished operations.</param>
    /// <param name="createdDirectories">The directories this transaction creates (not included in the operations).</param>
    [JsonConstructor]
    public JournalDocument(
        int version,
        Guid transactionId,
        bool committing,
        IReadOnlyList<JournalOperation>? operations = null,
        IReadOnlyList<string>? createdDirectories = null)
    {
        Version = version;
        TransactionId = transactionId;
        Committing = committing;
        Operations = operations ?? Array.Empty<JournalOperation>();
        CreatedDirectories = createdDirectories ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the version of the document format.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Gets the ID of the transaction.
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// Gets a value indicating whether the commit is being applied.
    /// </summary>
    public bool Committing { get; }

    /// <summary>
    /// Gets the list of unfinished operations.
    /// </summary>
    public IReadOnlyList<JournalOperation> Operations { get; }

    /// <summary>
    /// Gets the directories this transaction creates (not included in the operations).
    /// </summary>
    public IReadOnlyList<string> CreatedDirectories { get; }
}
