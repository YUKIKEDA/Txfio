namespace Txfio.Tests.Stress;

/// <summary>
/// The in-memory model that random sequences compare against. It holds the post-commit view.
/// </summary>
internal sealed class RandomOperationModel
{
    private readonly Dictionary<string, byte[]> _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="RandomOperationModel"/> class from the files at the start.
    /// </summary>
    /// <param name="initialFiles">A dictionary from relative path to content.</param>
    public RandomOperationModel(IReadOnlyDictionary<string, byte[]> initialFiles)
    {
        _files = new Dictionary<string, byte[]>(initialFiles, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the files that should exist after commit (relative path to content). ReadAsync returns this content too.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>
    /// Applies a passed operation to the model.
    /// </summary>
    /// <param name="operation">The operation that passed.</param>
    public void Apply(RandomOperation operation)
    {
        operation.ApplyTo(_files);
    }
}
