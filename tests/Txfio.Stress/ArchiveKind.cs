namespace Txfio.Tests.Stress;

/// <summary>
/// The kinds of operations used in random ZIP sequences.
/// </summary>
internal enum ArchiveKind
{
    /// <summary>
    /// CreateArchiveAsync inside the work folder.
    /// </summary>
    Create,

    /// <summary>
    /// ExtractArchiveAsync inside the work folder.
    /// </summary>
    Extract,

    /// <summary>
    /// ImportArchiveAsync, which imports an external ZIP.
    /// </summary>
    Import,

    /// <summary>
    /// ExportArchiveAsync, which writes a ZIP outside.
    /// </summary>
    Export,
}
