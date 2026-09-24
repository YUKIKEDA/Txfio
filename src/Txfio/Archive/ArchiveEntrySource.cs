namespace Txfio;

/// <summary>
/// ZIP に入れるファイルまたはディレクトリと、ZIP の中での名前の組
/// </summary>
/// <param name="SourcePath">入れるファイルまたはディレクトリ（ワークフォルダ基準の相対、またはワークフォルダ内の絶対パス）</param>
/// <param name="EntryName">ZIP の中での名前。null のときはワークフォルダからの相対パス。ディレクトリで空文字のときは中身を ZIP のルートに置く</param>
public sealed record ArchiveEntrySource(string SourcePath, string? EntryName = null);
