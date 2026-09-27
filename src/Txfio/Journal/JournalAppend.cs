using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// One line appended to the end of the journal (operations and created directories added to the end of the table).
/// </summary>
internal sealed class JournalAppend
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalAppend"/> class with the added operations and created directories.
    /// </summary>
    /// <param name="append">The operations added to the end of the table (<see langword="null"/> if none).</param>
    /// <param name="createdDirectories">The created directories added to the end (<see langword="null"/> if none).</param>
    [JsonConstructor]
    public JournalAppend(
        IReadOnlyList<JournalOperation>? append = null,
        IReadOnlyList<string>? createdDirectories = null)
    {
        Append = append ?? Array.Empty<JournalOperation>();
        CreatedDirectories = createdDirectories ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the operations added to the end of the table.
    /// </summary>
    public IReadOnlyList<JournalOperation> Append { get; }

    /// <summary>
    /// Gets the created directories added to the end.
    /// </summary>
    public IReadOnlyList<string> CreatedDirectories { get; }
}
