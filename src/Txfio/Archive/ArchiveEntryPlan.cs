using System.IO.Compression;

namespace Txfio;

/// <summary>
/// 検証済みのエントリ 1 つの展開予定
/// </summary>
/// <param name="Entry">ZIP のエントリ</param>
/// <param name="RelativePath">展開先からの相対パス</param>
/// <param name="IsDirectory">ディレクトリエントリなら <see langword="true"/></param>
internal sealed record ArchiveEntryPlan(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
