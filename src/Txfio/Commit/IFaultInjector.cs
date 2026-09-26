namespace Txfio;

/// <summary>
/// テストがトランザクションごとに渡す、失敗と途中停止
/// </summary>
internal interface IFaultInjector
{
    /// <summary>
    /// <c>Committing</c> を書いた直後
    /// </summary>
    const string AfterCommitting = "AfterCommitting";

    /// <summary>
    /// 適用順の各操作が成功した直後
    /// </summary>
    const string AfterApply = "AfterApply";

    /// <summary>
    /// コミットを途中で止めたあと、またはロールバックを止めたあとは <see langword="true"/>（そのとき Dispose はロールバックしない）
    /// </summary>
    bool ShouldSkipRollback { get; }

    /// <summary>
    /// 指定した地点に達していれば、止めたことにして例外を投げる
    /// </summary>
    /// <param name="name">現在の地点</param>
    void CheckPoint(string name);

    /// <summary>
    /// 次の適用に仕込んだ例外があれば、それを一度だけ投げる
    /// </summary>
    void ThrowIfApplyArmed();

    /// <summary>
    /// 次に仕込んだ共有モードで開くとき、仕込んだ例外があればそれを投げる
    /// </summary>
    /// <param name="share">開く共有モード</param>
    void ThrowIfOpenArmed(FileShare share);
}
