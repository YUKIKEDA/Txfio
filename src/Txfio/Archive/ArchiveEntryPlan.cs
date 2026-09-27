using System.IO.Compression;

namespace Txfio;

/// <summary>
/// The plan to extract one checked entry.
/// </summary>
/// <param name="Entry">The ZIP entry.</param>
/// <param name="RelativePath">The path relative to the destination.</param>
/// <param name="IsDirectory"><see langword="true"/> for a directory entry.</param>
internal sealed record ArchiveEntryPlan(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
