namespace Txfio;

/// <summary>
/// ジャーナルを読んだ結果（読めた文書、または読めない理由）
/// </summary>
internal sealed class JournalReadResult
{
    private JournalReadResult(JournalDocument? document, bool unsupportedVersion)
    {
        Document = document;
        UnsupportedVersion = unsupportedVersion;
    }

    /// <summary>
    /// 読めた文書（読めないときは <see langword="null"/>）
    /// </summary>
    internal JournalDocument? Document { get; }

    /// <summary>
    /// 版がこのライブラリの版と違うなら <see langword="true"/>（新しい版が残したものかもしれないので何にも触れない）
    /// </summary>
    internal bool UnsupportedVersion { get; }

    /// <summary>
    /// 読めた文書の結果を作る
    /// </summary>
    /// <param name="document">読めた文書</param>
    /// <returns>読めた結果</returns>
    internal static JournalReadResult Readable(JournalDocument document)
    {
        return new JournalReadResult(document, unsupportedVersion: false);
    }

    /// <summary>
    /// 壊れていて読めない結果を作る
    /// </summary>
    /// <returns>壊れている結果</returns>
    internal static JournalReadResult Corrupt()
    {
        return new JournalReadResult(document: null, unsupportedVersion: false);
    }

    /// <summary>
    /// 版が違って読めない結果を作る
    /// </summary>
    /// <returns>版が違う結果</returns>
    internal static JournalReadResult OtherVersion()
    {
        return new JournalReadResult(document: null, unsupportedVersion: true);
    }
}
