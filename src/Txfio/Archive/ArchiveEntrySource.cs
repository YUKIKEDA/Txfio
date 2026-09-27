namespace Txfio;

/// <summary>
/// A pair of a file or directory to put in a ZIP and its name inside the ZIP.
/// </summary>
/// <param name="SourcePath">The file or directory to add (relative to the work folder, or absolute inside the work folder).</param>
/// <param name="EntryName">The name inside the ZIP (when <see langword="null"/>, the path relative to the work folder; an empty string for a directory puts its contents at the root of the ZIP).</param>
public sealed record ArchiveEntrySource(string SourcePath, string? EntryName = null);
