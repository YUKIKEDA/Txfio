namespace Txfio.Tests.Stress;

/// <summary>
/// ZIP のランダム列で使う操作の種類
/// </summary>
internal enum ArchiveKind
{
    /// <summary>
    /// ワークフォルダ内の CreateArchiveAsync
    /// </summary>
    Create,

    /// <summary>
    /// ワークフォルダ内の ExtractArchiveAsync
    /// </summary>
    Extract,

    /// <summary>
    /// 外の ZIP を取り込む ImportArchiveAsync
    /// </summary>
    Import,

    /// <summary>
    /// 外へ ZIP を書く ExportArchiveAsync
    /// </summary>
    Export,
}
