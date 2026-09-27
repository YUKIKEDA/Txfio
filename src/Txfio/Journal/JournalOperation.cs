using System.Text.Json.Serialization;

namespace Txfio;

/// <summary>
/// One operation recorded in the journal.
/// </summary>
internal sealed class JournalOperation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalOperation"/> class with the operation kind and paths.
    /// </summary>
    /// <param name="kind">The operation kind.</param>
    /// <param name="path">The target path.</param>
    /// <param name="stagingPath">The path of the staging file (<c>.txnew</c>) (<see langword="null"/> except for Add / Update).</param>
    /// <param name="newPath">The Move destination (<see langword="null"/> otherwise).</param>
    /// <param name="before">The state of the target path just before apply (<see langword="null"/> if not recorded).</param>
    /// <param name="after">The state of the target path just after apply (<see langword="null"/> if not recorded).</param>
    /// <param name="destBefore">The state of the Move destination just before apply (<see langword="null"/> otherwise).</param>
    /// <param name="destAfter">The state of the Move destination just after apply (<see langword="null"/> otherwise).</param>
    /// <param name="isDirectory"><see langword="true"/> for a directory Delete, DeleteTree, Move, or CreateDirectory.</param>
    /// <param name="directoryCreated"><see langword="true"/> once CreateDirectory has created the directory.</param>
    /// <param name="overwrite"><see langword="true"/> for a replacing Move.</param>
    [JsonConstructor]
    public JournalOperation(
        PendingChangeKind kind,
        string path,
        string? stagingPath = null,
        string? newPath = null,
        PathState? before = null,
        PathState? after = null,
        PathState? destBefore = null,
        PathState? destAfter = null,
        bool isDirectory = false,
        bool directoryCreated = false,
        bool overwrite = false)
    {
        Kind = kind;
        Path = path;
        StagingPath = stagingPath;
        NewPath = newPath;
        Before = before;
        After = after;
        DestBefore = destBefore;
        DestAfter = destAfter;
        IsDirectory = isDirectory;
        DirectoryCreated = directoryCreated;
        Overwrite = overwrite;
    }

    /// <summary>
    /// Gets the operation kind.
    /// </summary>
    public PendingChangeKind Kind { get; }

    /// <summary>
    /// Gets the target path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the path of the staging file (<c>.txnew</c>) (<see langword="null"/> except for Add / Update).
    /// </summary>
    public string? StagingPath { get; }

    /// <summary>
    /// Gets the Move destination path.
    /// </summary>
    public string? NewPath { get; }

    /// <summary>
    /// Gets the state of the target path just before apply (<see langword="null"/> if not recorded).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? Before { get; }

    /// <summary>
    /// Gets the state of the target path just after apply (<see langword="null"/> if not recorded).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? After { get; }

    /// <summary>
    /// Gets the state of the Move destination just before apply (<see langword="null"/> otherwise).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? DestBefore { get; }

    /// <summary>
    /// Gets the state of the Move destination just after apply (<see langword="null"/> otherwise).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PathState? DestAfter { get; }

    /// <summary>
    /// Gets a value indicating whether this is a directory Delete, DeleteTree, Move, or CreateDirectory.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDirectory { get; }

    /// <summary>
    /// Gets a value indicating whether CreateDirectory has created the directory (if it crashed while not created, someone else may have created a directory with the same name).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DirectoryCreated { get; }

    /// <summary>
    /// Gets a value indicating whether this is a replacing Move.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Overwrite { get; }

    /// <summary>
    /// Returns a copy with Before / After.
    /// </summary>
    /// <param name="before">The state of the target path just before apply.</param>
    /// <param name="after">The state of the target path just after apply.</param>
    /// <param name="destBefore">The state of the Move destination just before apply.</param>
    /// <param name="destAfter">The state of the Move destination just after apply.</param>
    /// <returns>The operation with the states recorded.</returns>
    internal JournalOperation WithOutcome(
        PathState before,
        PathState after,
        PathState? destBefore = null,
        PathState? destAfter = null)
    {
        return new JournalOperation(
            Kind,
            Path,
            StagingPath,
            NewPath,
            before,
            after,
            destBefore,
            destAfter,
            IsDirectory,
            DirectoryCreated,
            Overwrite);
    }

    /// <summary>
    /// Returns a copy marked as created.
    /// </summary>
    /// <returns>The operation recorded as created.</returns>
    internal JournalOperation WithDirectoryCreated()
    {
        return new JournalOperation(
            Kind,
            Path,
            StagingPath,
            NewPath,
            Before,
            After,
            DestBefore,
            DestAfter,
            IsDirectory,
            directoryCreated: true,
            overwrite: Overwrite);
    }
}
