namespace Txfio.Tests.Stress;

/// <summary>
/// One ZIP step.
/// </summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Source">The input. A path relative to outside for Import, otherwise relative to the work folder.</param>
/// <param name="Destination">The output. A path relative to outside for Export, otherwise relative to the work folder.</param>
/// <param name="IncludeBase">When a directory becomes a ZIP, whether its name goes at the start of the entries.</param>
internal sealed record ArchiveOperation(ArchiveKind Kind, string Source, string Destination, bool IncludeBase)
{
    /// <summary>
    /// Returns whether this step can be made in the current view.
    /// </summary>
    /// <param name="world">The view of the work folder and outside.</param>
    /// <returns>true when it can be made.</returns>
    public bool CanApply(ArchiveWorld world)
    {
        return world.CanApply(this);
    }

    /// <summary>
    /// Applies this step to the in-memory view.
    /// </summary>
    /// <param name="world">The view of the work folder and outside.</param>
    public void ApplyTo(ArchiveWorld world)
    {
        world.Apply(this);
    }

    /// <summary>
    /// A readable form for failure reports.
    /// </summary>
    /// <returns>The kind and paths.</returns>
    public override string ToString()
    {
        return Kind + "(" + Source + " -> " + Destination + (IncludeBase ? ", base" : string.Empty) + ")";
    }
}
