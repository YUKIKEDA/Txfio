namespace Txfio.Tests.Stress;

/// <summary>
/// One copy, import, or export step.
/// </summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Source">The source. Relative to the work folder for Copy and Export, relative to outside for Import.</param>
/// <param name="Destination">The destination. Relative to the work folder for Copy and Import, relative to outside for Export.</param>
internal sealed record TransferOperation(TransferKind Kind, string Source, string Destination)
{
    /// <summary>
    /// Returns whether this step can be made in the current view.
    /// </summary>
    /// <param name="world">The view of the work folder and outside.</param>
    /// <returns>true when it can be made.</returns>
    public bool CanApply(TransferWorld world)
    {
        return world.CanApply(this);
    }

    /// <summary>
    /// Applies this step to the in-memory view.
    /// </summary>
    /// <param name="world">The view of the work folder and outside.</param>
    public void ApplyTo(TransferWorld world)
    {
        world.Apply(this);
    }

    /// <summary>
    /// A readable form for failure reports.
    /// </summary>
    /// <returns>The kind and paths.</returns>
    public override string ToString()
    {
        return Kind + "(" + Source + " -> " + Destination + ")";
    }
}
